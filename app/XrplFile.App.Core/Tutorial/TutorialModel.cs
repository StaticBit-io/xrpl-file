using XrplFile.App.Core.Localization;
using R = XrplFile.App.Core.Tutorial.TutorialRegions;

namespace XrplFile.App.Core.Tutorial;

/// <summary>Whether a topic can start now, and when it cannot, the reason to show under its line.</summary>
public sealed record TopicAvailability(bool CanStart, UiText? Reason);

/// <summary>One bubble as the overlay draws it.</summary>
/// <param name="Note">A line added under the body by what happened on the screen, or null.</param>
/// <param name="Number">Position of the step, from 1.</param>
/// <param name="FinishStartsTopic">The topic the last step's Finish button starts after leaving, or null.</param>
public sealed record TutorialView(
    TextId TopicName,
    UiText Title,
    UiText Body,
    UiText? Note,
    int Number,
    int Count,
    IReadOnlyList<string> Lit,
    IReadOnlyList<string> Usable,
    bool IsLast,
    TutorialTopic? FinishStartsTopic);

/// <summary>
/// Everything the tutorial decides: the first-run offer, the topic list and which lines can start,
/// the active step and the screen it needs, moving on by Next or by what the Read tab reports, and
/// leaving. Nothing here draws; the overlay binds to <see cref="Current"/>.
/// </summary>
public sealed class TutorialModel(
    ITutorialSettingsStore store,
    ShellModel shell,
    ScreenNavigation nav,
    ITutorialReadTab readTab,
    Func<TutorialFacts> facts)
{
    private readonly object _gate = new();

    private bool _offeredThisRun;
    private bool _listOpen;
    private bool _listIsOffer;
    private bool _active;
    private TutorialTopic _topic;
    private IReadOnlyList<TutorialStep> _steps = [];
    private int _index;
    private AppTab _startTab;
    private bool _readIsReal;
    private bool _readStarted;
    private TutorialTopic? _finishStarts;
    private TutorialView? _view;

    /// <summary>Whether the topic list dialog is showing.</summary>
    public bool ListOpen
    {
        get { lock (_gate) return _listOpen; }
    }

    /// <summary>Whether the list is the first-run offer, with its welcome line and Not now.</summary>
    public bool ListIsOffer
    {
        get { lock (_gate) return _listIsOffer; }
    }

    /// <summary>Whether a topic is running.</summary>
    public bool IsActive
    {
        get { lock (_gate) return _active; }
    }

    /// <summary>The bubble to draw, or null when no topic runs.</summary>
    public TutorialView? Current
    {
        get { lock (_gate) return _view; }
    }

    /// <summary>Raised once per observable move, outside any lock.</summary>
    public event Action? Changed;

    /// <summary>
    /// Opens the list as the first-run offer when the vault holds no key and the offer was never
    /// shown. The flag is written before the offer is answered, so a crash or a closed window does
    /// not bring it back; a failed write only means the next start offers again, and the in-memory
    /// guard keeps this run from offering twice either way.
    /// </summary>
    public void OfferIfDue()
    {
        lock (_gate)
        {
            if (_offeredThisRun || _active || _listOpen)
            {
                return;
            }

            if (store.WasOffered() || facts().KeyCount > 0)
            {
                return;
            }

            _offeredThisRun = true;
            store.TryMarkOffered();
            _listOpen = true;
            _listIsOffer = true;
        }

        Changed?.Invoke();
    }

    /// <summary>Opens the topic list. Ignored while a topic runs or the list is already open.</summary>
    public void OpenList()
    {
        lock (_gate)
        {
            if (_active || _listOpen)
            {
                return;
            }

            _listOpen = true;
            _listIsOffer = false;
        }

        Changed?.Invoke();
    }

    /// <summary>Closes the list, whether it was the offer or not.</summary>
    public void CloseList()
    {
        lock (_gate)
        {
            if (!_listOpen)
            {
                return;
            }

            _listOpen = false;
            _listIsOffer = false;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Whether <paramref name="topic"/> can start now. A topic that would collide with work in
    /// progress is blocked, not queued, and says why.
    /// </summary>
    public TopicAvailability AvailabilityOf(TutorialTopic topic) => AvailabilityOf(topic, facts());

    /// <summary>
    /// Starts <paramref name="topic"/> from its first step. Returns false, changing nothing, when a
    /// topic already runs or this one is unavailable. Every sub-screen is put back on its main screen
    /// first, the tab already showing included, because moving to the same tab does nothing.
    /// </summary>
    public bool Start(TutorialTopic topic)
    {
        TutorialFacts now = facts();
        TutorialStep first;
        bool putLink;

        lock (_gate)
        {
            if (_active || !AvailabilityOf(topic, now).CanStart)
            {
                return false;
            }

            // The Read mode is fixed here: a tab that holds anything is never touched, even if it
            // empties while the topic runs.
            _readIsReal = topic == TutorialTopic.Read && now.ReadState == ReadState.Idle && now.ReadTabEmpty;
            _readStarted = false;
            _finishStarts = null;

            if (topic == TutorialTopic.ExportKey && now.KeyCount == 0)
            {
                _steps = TutorialScript.ExportWithoutKey;
                _finishStarts = TutorialTopic.CreateAccount;
            }
            else if (topic == TutorialTopic.Read && !_readIsReal)
            {
                _steps = TutorialScript.ReadShown;
            }
            else
            {
                _steps = TutorialScript.StepsOf(topic);
            }

            _topic = topic;
            _index = 0;
            _startTab = shell.Tab;
            _active = true;
            _listOpen = false;
            _listIsOffer = false;
            _view = BuildView(now);
            first = _steps[0];
            putLink = _readIsReal;
        }

        nav.ResetToMain();
        Navigate(first, now);

        if (putLink)
        {
            readTab.PutLink(TutorialScript.SampleLink);
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Moves to the next step; on the last step it leaves.</summary>
    public void Next()
    {
        TutorialFacts now = facts();
        TutorialStep? step = null;

        lock (_gate)
        {
            if (!_active)
            {
                return;
            }

            if (_index < _steps.Count - 1)
            {
                _index++;
                _view = BuildView(now);
                step = _steps[_index];
            }
        }

        if (step is null)
        {
            Leave();
            return;
        }

        Navigate(step, now);
        Changed?.Invoke();
    }

    /// <summary>
    /// Leave, Finish and Escape: back to the tab the topic started from, on its main screen. A Read
    /// topic done for real undoes only what it did itself.
    /// </summary>
    public void Leave()
    {
        if (!Stop(out AppTab returnTo))
        {
            return;
        }

        shell.Show(returnTo);
        nav.ResetToMain();
        Changed?.Invoke();
    }

    /// <summary>Finish of the export topic without a key: leaves, then starts creating an account.</summary>
    public void FinishInto()
    {
        TutorialTopic? next;

        lock (_gate)
        {
            next = _active ? _finishStarts : null;
        }

        Leave();

        if (next is TutorialTopic topic)
        {
            Start(topic);
        }
    }

    /// <summary>
    /// Ends the topic without moving anywhere: the vault locked and the shell is already on its way
    /// to the unlock screen.
    /// </summary>
    public void End()
    {
        bool listWasOpen;

        lock (_gate)
        {
            listWasOpen = _listOpen;
            _listOpen = false;
            _listIsOffer = false;
        }

        bool wasActive = Stop(out _);

        if (wasActive || listWasOpen)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Re-reads the facts and moves the topic on by itself when the Read tab says its lookup or read
    /// finished. Returns true when the bubble changed.
    /// </summary>
    public bool Settle()
    {
        TutorialFacts now = facts();
        TutorialStep? step = null;
        bool changed;

        lock (_gate)
        {
            if (!_active)
            {
                return false;
            }

            if (_readIsReal && now.ReadState != ReadState.Idle)
            {
                _readStarted = true;
            }

            TutorialAdvance advance = _steps[_index].Advance;
            bool moves = _readIsReal && _index < _steps.Count - 1
                && ((advance == TutorialAdvance.OnLookupSucceeded && now.LookupSucceeded)
                    || (advance == TutorialAdvance.OnReadSucceeded && now.ReadState == ReadState.Succeeded));

            TutorialView? before = _view;

            if (moves)
            {
                _index++;
                step = _steps[_index];
            }

            _view = BuildView(now);
            changed = !SameView(before, _view);
        }

        if (step is not null)
        {
            Navigate(step, now);
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return changed;
    }

    private static TopicAvailability AvailabilityOf(TutorialTopic topic, TutorialFacts now)
    {
        if (topic == TutorialTopic.Publish && now.PublicationRunning)
        {
            return new TopicAvailability(false, TutorialText.BusyPublishing);
        }

        if (topic == TutorialTopic.Read && now.ReadState == ReadState.Running)
        {
            return new TopicAvailability(false, TutorialText.BusyReading);
        }

        return new TopicAvailability(true, null);
    }

    /// <summary>Ends the topic and undoes what a real Read topic did. Does not navigate or raise <see cref="Changed"/>.</summary>
    private bool Stop(out AppTab returnTo)
    {
        TutorialFacts now = facts();
        bool cancel = false;
        bool clear = false;

        lock (_gate)
        {
            returnTo = _startTab;

            if (!_active)
            {
                return false;
            }

            if (_readIsReal)
            {
                bool started = _readStarted || now.ReadState != ReadState.Idle;

                // A read still running is the tutorial's own. One that finished stays like any read
                // the user made, and a box the tutorial filled but never read from is emptied.
                cancel = now.ReadState == ReadState.Running;
                clear = !started;
            }

            _active = false;
            _view = null;
            _steps = [];
            _index = 0;
            _readIsReal = false;
            _readStarted = false;
            _finishStarts = null;
        }

        if (cancel)
        {
            readTab.CancelRead();
        }

        if (clear)
        {
            readTab.Clear();
        }

        return true;
    }

    /// <summary>Shows the screen <paramref name="step"/> needs. Called outside the lock: the screens redraw on these changes.</summary>
    private void Navigate(TutorialStep step, TutorialFacts now)
    {
        TutorialScreen screen = step.Screen;
        shell.Show(screen.Tab);

        if (screen.Tab != AppTab.Accounts)
        {
            return;
        }

        string? reference = now.NotExportedReference ?? now.FirstAccountReference;
        AccountsView view = screen.Accounts;

        // No row read yet: stay on the list rather than open a screen with nothing to act on.
        if (view == AccountsView.Export && reference is null)
        {
            view = AccountsView.List;
        }

        nav.OpenAccounts(view, view == AccountsView.Export ? reference : null);

        if (view == AccountsView.AddKey)
        {
            nav.ShowImportTab(screen.Import);
        }

        if (screen.Menu == TutorialMenu.ExportRow && reference is not null)
        {
            nav.OpenMenu(reference);
        }
    }

    private TutorialView BuildView(TutorialFacts now)
    {
        TutorialStep step = _steps[_index];
        (string, object)[] buttons = [.. step.Buttons.Select(button => (button.Name, (object)button.Button))];

        // A key that was never exported is the point of the step; when every key has been, the first row stands in.
        IReadOnlyList<string> lit = step.Lit;
        if (_topic == TutorialTopic.ExportKey && _finishStarts is null && _index == 0 && now.NotExportedReference is null)
        {
            lit = [R.AccountsRow];
        }

        UiText? note = null;
        if (_readIsReal && _index == 0 && now.LookupFailed)
        {
            note = TutorialText.LookupFailedNote;
        }
        else if (_readIsReal && _index == 1 && now.ReadState is ReadState.Failed or ReadState.Cancelled)
        {
            note = TutorialText.ReadFailedNote;
        }

        return new TutorialView(
            TutorialScript.NameOf(_topic),
            UiText.Of(step.Title, buttons),
            UiText.Of(step.Body, buttons),
            note,
            _index + 1,
            _steps.Count,
            lit,
            step.Usable,
            _index == _steps.Count - 1,
            _finishStarts);
    }

    private static bool SameView(TutorialView? a, TutorialView? b) =>
        a is not null && b is not null
        && a.Title.Equals(b.Title) && Equals(a.Note, b.Note) && a.Number == b.Number
        && a.TopicName == b.TopicName && a.Lit.SequenceEqual(b.Lit, StringComparer.Ordinal);
}
