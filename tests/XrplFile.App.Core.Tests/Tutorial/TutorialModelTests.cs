using XrplFile.App.Core.Localization;
using XrplFile.App.Core.Tutorial;
using Xunit;

namespace XrplFile.App.Core.Tests.Tutorial;

public sealed class TutorialModelTests
{
    private const string NotExported = "ref-not-exported";
    private const string First = "ref-first";

    private readonly FakeTutorialSettings _settings = new();
    private readonly FakeReadTab _readTab = new();
    private readonly ShellModel _shell = new();
    private readonly ScreenNavigation _nav;
    private readonly TutorialModel _model;

    // The Func the model reads; tests move it between calls.
    private TutorialFacts _facts = new(0, null, null, false, ReadState.Idle, true, false, false);

    public TutorialModelTests()
    {
        _nav = new ScreenNavigation(_shell);
        _model = new TutorialModel(_settings, _shell, _nav, _readTab, () => _facts);
    }

    private void WithKeys(string? first = First, string? notExported = NotExported) =>
        _facts = _facts with { KeyCount = 1, FirstAccountReference = first, NotExportedReference = notExported };

    private TutorialView View => _model.Current!;

    private void NextTimes(int count)
    {
        for (int i = 0; i < count; i++) _model.Next();
    }

    [Fact]
    public void Offers_once_when_the_vault_holds_no_key()
    {
        _model.OfferIfDue();

        Assert.True(_model.ListOpen);
        Assert.True(_model.ListIsOffer);
        Assert.Equal(1, _settings.Marks);

        _model.CloseList();
        _model.OfferIfDue();

        Assert.False(_model.ListOpen);
        Assert.Equal(1, _settings.Marks);
    }

    [Fact]
    public void Does_not_offer_with_any_key()
    {
        // A damaged entry counts in KeyCount: the vault was used.
        _facts = _facts with { KeyCount = 1 };

        _model.OfferIfDue();

        Assert.False(_model.ListOpen);
        Assert.Equal(0, _settings.Marks);
    }

    [Fact]
    public void Does_not_offer_when_already_offered()
    {
        _settings.Offered = true;

        _model.OfferIfDue();

        Assert.False(_model.ListOpen);
        Assert.Equal(0, _settings.Marks);
    }

    [Fact]
    public void A_failed_flag_write_still_offers_only_once_this_run()
    {
        _settings.MarkSucceeds = false;

        _model.OfferIfDue();
        Assert.True(_model.ListOpen);

        _model.CloseList();
        _model.OfferIfDue();

        Assert.False(_model.ListOpen);
        Assert.Equal(1, _settings.Marks);
    }

    [Fact]
    public void NoticeStage_offers_when_the_vault_unlocks_with_no_key()
    {
        _model.NoticeStage(UnlockStage.Unlocked);

        Assert.True(_model.ListOpen);
        Assert.True(_model.ListIsOffer);
    }

    [Fact]
    public void NoticeStage_does_not_offer_when_the_last_key_goes_during_the_session()
    {
        WithKeys();
        _model.NoticeStage(UnlockStage.Unlocked);

        _facts = _facts with { KeyCount = 0, FirstAccountReference = null, NotExportedReference = null };
        _model.NoticeStage(UnlockStage.Unlocked);

        Assert.False(_model.ListOpen);
        Assert.Equal(0, _settings.Marks);
    }

    [Fact]
    public void NoticeStage_offers_on_the_next_unlock_when_not_yet_offered()
    {
        WithKeys();
        _model.NoticeStage(UnlockStage.Unlocked);
        _model.NoticeStage(UnlockStage.Locked);
        _facts = _facts with { KeyCount = 0, FirstAccountReference = null, NotExportedReference = null };

        _model.NoticeStage(UnlockStage.Unlocked);
        Assert.True(_model.ListOpen);
        Assert.Equal(1, _settings.Marks);

        _model.CloseList();
        _model.NoticeStage(UnlockStage.Locked);
        _model.NoticeStage(UnlockStage.Unlocked);

        Assert.False(_model.ListOpen);
        Assert.Equal(1, _settings.Marks);
    }

    [Fact]
    public void NoticeStage_reads_nothing_while_the_stage_stays_the_same()
    {
        int reads = 0;
        TutorialModel model = new(_settings, _shell, _nav, _readTab, () =>
        {
            reads++;
            return _facts with { KeyCount = 1 };
        });

        model.NoticeStage(UnlockStage.Unlocked);
        int afterEntering = reads;
        model.NoticeStage(UnlockStage.Unlocked);
        model.NoticeStage(UnlockStage.Unlocked);

        Assert.Equal(afterEntering, reads);
    }

    [Fact]
    public void NoticeStage_ignores_stages_other_than_unlocked()
    {
        _model.NoticeStage(UnlockStage.NoVault);
        _model.NoticeStage(UnlockStage.Locked);

        Assert.False(_model.ListOpen);
        Assert.Equal(0, _settings.Marks);
    }

    [Fact]
    public void The_offer_flag_is_written_outside_the_lock()
    {
        bool? readable = null;
        _settings.OnMark = () => readable = Task.Run(() => _model.ListOpen).Wait(TimeSpan.FromSeconds(2));

        _model.OfferIfDue();

        Assert.True(readable);
        Assert.True(_model.ListOpen);
        Assert.Equal(1, _settings.Marks);
    }

    [Fact]
    public void Opening_the_list_closes_More()
    {
        _shell.ToggleMore();

        _model.OpenList();

        Assert.False(_shell.MoreOpen);
    }

    [Fact]
    public void The_offer_closes_More()
    {
        _shell.ToggleMore();

        _model.OfferIfDue();

        Assert.False(_shell.MoreOpen);
    }

    [Fact]
    public void Starting_a_topic_closes_More()
    {
        _shell.ToggleMore();

        _model.Start(TutorialTopic.AddKey);

        Assert.False(_shell.MoreOpen);
    }

    [Fact]
    public void MarkedRow_is_the_latched_export_row_only_while_a_topic_runs()
    {
        WithKeys();
        Assert.Null(_model.MarkedRow);

        _model.Start(TutorialTopic.ExportKey);
        Assert.Equal(NotExported, _model.MarkedRow);

        _facts = _facts with { NotExportedReference = "ref-later" };
        _model.Settle();
        Assert.Equal(NotExported, _model.MarkedRow);

        _model.Leave();
        Assert.Null(_model.MarkedRow);
    }

    [Fact]
    public void MarkedRow_falls_back_to_the_first_row_when_every_key_was_exported()
    {
        WithKeys(notExported: null);

        _model.Start(TutorialTopic.ExportKey);

        Assert.Equal(First, _model.MarkedRow);
    }

    [Fact]
    public void MarkedFundRow_is_the_key_only_row_while_a_topic_runs()
    {
        _facts = _facts with { KeyCount = 2, FirstAccountReference = First, KeyOnlyReference = "ref-key-only" };
        Assert.Null(_model.MarkedFundRow);

        _model.Start(TutorialTopic.CreateAccount);
        Assert.Equal("ref-key-only", _model.MarkedFundRow);

        _facts = _facts with { KeyOnlyReference = null };
        Assert.Null(_model.MarkedFundRow);
    }

    [Fact]
    public void OpenList_is_not_an_offer()
    {
        _model.OpenList();

        Assert.True(_model.ListOpen);
        Assert.False(_model.ListIsOffer);
        Assert.Equal(0, _settings.Marks);
    }

    [Fact]
    public void OpenList_is_ignored_while_a_topic_runs()
    {
        _model.Start(TutorialTopic.AddKey);

        _model.OpenList();

        Assert.False(_model.ListOpen);
    }

    [Fact]
    public void Publish_is_unavailable_while_a_publication_runs()
    {
        _facts = _facts with { PublicationRunning = true };

        TopicAvailability availability = _model.AvailabilityOf(TutorialTopic.Publish);

        Assert.False(availability.CanStart);
        Assert.Equal(UiText.Of(TutorialText.BusyPublishing), availability.Reason);
        Assert.True(_model.AvailabilityOf(TutorialTopic.Read).CanStart);
    }

    [Fact]
    public void AvailabilityOfAll_follows_topic_order_and_reads_the_facts_once()
    {
        int reads = 0;
        TutorialModel model = new(_settings, _shell, _nav, _readTab, () =>
        {
            reads++;
            return _facts with { PublicationRunning = true };
        });

        IReadOnlyList<TopicAvailability> all = model.AvailabilityOfAll();

        Assert.Equal(1, reads);
        Assert.Equal(TutorialScript.Topics.Count, all.Count);
        for (int i = 0; i < all.Count; i++)
        {
            Assert.Equal(TutorialScript.Topics[i] != TutorialTopic.Publish, all[i].CanStart);
        }
    }

    [Fact]
    public void Read_is_unavailable_while_a_read_runs()
    {
        _facts = _facts with { ReadState = ReadState.Running };

        TopicAvailability availability = _model.AvailabilityOf(TutorialTopic.Read);

        Assert.False(availability.CanStart);
        Assert.Equal(UiText.Of(TutorialText.BusyReading), availability.Reason);
        Assert.True(_model.AvailabilityOf(TutorialTopic.Publish).CanStart);
    }

    [Fact]
    public void Start_refuses_an_unavailable_topic()
    {
        _facts = _facts with { PublicationRunning = true };
        _model.OpenList();
        int changes = 0;
        _model.Changed += () => changes++;

        bool started = _model.Start(TutorialTopic.Publish);

        Assert.False(started);
        Assert.False(_model.IsActive);
        Assert.True(_model.ListOpen);
        Assert.Equal(AppTab.Accounts, _shell.Tab);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Start_while_active_is_ignored()
    {
        _model.Start(TutorialTopic.AddKey);

        Assert.False(_model.Start(TutorialTopic.CreateAccount));
        Assert.Equal(TutorialScript.NameOf(TutorialTopic.AddKey), View.TopicName);
    }

    [Fact]
    public void Start_closes_the_list_and_resets_the_tab_already_showing()
    {
        _nav.OpenAccounts(AccountsView.New);
        _model.OpenList();

        Assert.True(_model.Start(TutorialTopic.CreateAccount));

        Assert.False(_model.ListOpen);
        Assert.True(_model.IsActive);
        Assert.Equal(AppTab.Accounts, _shell.Tab);
        Assert.Equal(AccountsView.List, _nav.AccountsView);
    }

    [Fact]
    public void Steps_drive_tab_and_sub_screen()
    {
        _model.Start(TutorialTopic.CreateAccount);
        NextTimes(2);

        Assert.Equal(AppTab.Accounts, _shell.Tab);
        Assert.Equal(AccountsView.New, _nav.AccountsView);
        Assert.Equal(3, View.Number);

        NextTimes(2);

        Assert.Equal(AccountsView.List, _nav.AccountsView);

        _model.Leave();
        _model.Start(TutorialTopic.AddKey);
        NextTimes(2);

        Assert.Equal(AccountsView.AddKey, _nav.AccountsView);
        Assert.Equal(ImportTab.Backup, _nav.ImportTab);

        _model.Leave();
        _model.Start(TutorialTopic.Publish);

        Assert.Equal(AppTab.Publish, _shell.Tab);
    }

    [Fact]
    public void Leave_returns_to_the_starting_tab_on_its_main_screen()
    {
        _shell.Show(AppTab.History);
        _model.Start(TutorialTopic.CreateAccount);
        NextTimes(2);
        Assert.Equal(AppTab.Accounts, _shell.Tab);
        Assert.Equal(AccountsView.New, _nav.AccountsView);

        _model.Leave();

        Assert.False(_model.IsActive);
        Assert.Null(_model.Current);
        Assert.Equal(AppTab.History, _shell.Tab);
        Assert.Equal(AccountsView.List, _nav.AccountsView);
    }

    [Fact]
    public void Leave_from_the_tab_already_showing_still_closes_the_sub_screen()
    {
        _model.Start(TutorialTopic.CreateAccount);
        NextTimes(2);

        _model.Leave();

        Assert.Equal(AppTab.Accounts, _shell.Tab);
        Assert.Equal(AccountsView.List, _nav.AccountsView);
    }

    [Fact]
    public void Finish_on_the_last_step_leaves()
    {
        _model.Start(TutorialTopic.AddKey);
        NextTimes(3);
        Assert.True(View.IsLast);

        _model.Next();

        Assert.False(_model.IsActive);
    }

    [Fact]
    public void End_stops_without_navigating()
    {
        _shell.Show(AppTab.History);
        _model.Start(TutorialTopic.CreateAccount);
        NextTimes(2);

        _model.End();

        Assert.False(_model.IsActive);
        Assert.Equal(AppTab.Accounts, _shell.Tab);
        Assert.Equal(AccountsView.New, _nav.AccountsView);
    }

    [Fact]
    public void End_closes_an_open_list()
    {
        _model.OpenList();

        _model.End();

        Assert.False(_model.ListOpen);
    }

    [Fact]
    public void Export_without_a_key_is_one_step_that_leads_to_create_account()
    {
        _model.Start(TutorialTopic.ExportKey);

        Assert.Equal(1, View.Count);
        Assert.True(View.IsLast);
        Assert.Equal(TutorialTopic.CreateAccount, View.FinishStartsTopic);

        _model.FinishInto();

        Assert.True(_model.IsActive);
        Assert.Equal(TutorialScript.NameOf(TutorialTopic.CreateAccount), View.TopicName);
        Assert.Equal(1, View.Number);
        Assert.Equal(6, View.Count);
    }

    [Fact]
    public void Export_with_keys_has_no_finish_target()
    {
        WithKeys();
        _model.Start(TutorialTopic.ExportKey);

        Assert.Equal(4, View.Count);
        Assert.Null(View.FinishStartsTopic);
    }

    [Fact]
    public void Export_lights_the_not_exported_tag_when_one_exists_else_the_first_row()
    {
        WithKeys();
        _model.Start(TutorialTopic.ExportKey);
        Assert.Equal([TutorialRegions.AccountsNotExported], View.Lit);
        _model.Leave();

        WithKeys(notExported: null);
        _model.Start(TutorialTopic.ExportKey);
        Assert.Equal([TutorialRegions.AccountsRow], View.Lit);
    }

    [Fact]
    public void Export_opens_the_menu_and_screen_of_its_row()
    {
        WithKeys();
        _model.Start(TutorialTopic.ExportKey);

        _model.Next();
        Assert.Equal(NotExported, _nav.MenuFor);

        _model.Next();
        Assert.Equal(AccountsView.Export, _nav.AccountsView);
        Assert.Equal(NotExported, _nav.ActiveReference);
        Assert.Null(_nav.MenuFor);
    }

    [Fact]
    public void Export_falls_back_to_the_first_row_when_none_is_unexported()
    {
        WithKeys(notExported: null);
        _model.Start(TutorialTopic.ExportKey);

        _model.Next();
        Assert.Equal(First, _nav.MenuFor);

        _model.Next();
        Assert.Equal(First, _nav.ActiveReference);
    }

    [Fact]
    public void Export_with_keys_but_no_rows_read_yet_opens_nothing()
    {
        _facts = _facts with { KeyCount = 2 };
        _model.Start(TutorialTopic.ExportKey);

        _model.Next();
        Assert.Null(_nav.MenuFor);

        _model.Next();
        Assert.Equal(AccountsView.List, _nav.AccountsView);
        Assert.Null(_nav.ActiveReference);
    }

    [Fact]
    public void View_numbers_steps_and_fills_button_phrases()
    {
        _model.Start(TutorialTopic.CreateAccount);
        _model.Next();

        Assert.Equal(2, View.Number);
        Assert.Equal(6, View.Count);
        Assert.False(View.IsLast);
        Assert.Equal(TutorialScript.NameOf(TutorialTopic.CreateAccount), View.TopicName);
        Assert.Equal(UiText.Of(TutorialText.CreateAccountNewButtonTitle, ("newAccount", AccountsText.NewAccountButton)), View.Title);
        Assert.Equal(UiText.Of(TutorialText.CreateAccountNewButtonBody, ("newAccount", AccountsText.NewAccountButton)), View.Body);
        Assert.Equal([TutorialRegions.AccountsNew], View.Lit);
        Assert.Empty(View.Usable);
    }

    [Fact]
    public void Read_on_an_empty_idle_tab_puts_the_sample_link()
    {
        _model.Start(TutorialTopic.Read);

        Assert.Equal([TutorialScript.SampleLink], _readTab.Links);
        Assert.Equal([TutorialRegions.ReadLookup], View.Usable);
        Assert.Equal(AppTab.Read, _shell.Tab);
    }

    [Fact]
    public void Settle_that_changes_nothing_keeps_the_same_view_instance()
    {
        _model.Start(TutorialTopic.Read);
        TutorialView before = View;

        Assert.False(_model.Settle());
        Assert.Same(before, View);

        _facts = _facts with { ReadTabEmpty = false, LookupSucceeded = true };
        Assert.True(_model.Settle());
        Assert.NotSame(before, View);
    }

    [Fact]
    public void Read_moves_on_when_the_lookup_succeeds()
    {
        _model.Start(TutorialTopic.Read);
        _facts = _facts with { ReadTabEmpty = false, LookupSucceeded = true };

        Assert.True(_model.Settle());

        Assert.Equal(2, View.Number);
        Assert.Equal([TutorialRegions.ReadRead], View.Usable);
        Assert.False(_model.Settle());
    }

    [Fact]
    public void A_failed_lookup_adds_a_note_and_stays()
    {
        _model.Start(TutorialTopic.Read);
        _facts = _facts with { ReadTabEmpty = false, LookupFailed = true };

        Assert.True(_model.Settle());

        Assert.Equal(1, View.Number);
        Assert.Equal(UiText.Of(TutorialText.LookupFailedNote), View.Note);

        _model.Next();

        Assert.Equal(2, View.Number);
        Assert.Null(View.Note);
    }

    [Fact]
    public void Read_moves_on_when_the_read_succeeds()
    {
        _model.Start(TutorialTopic.Read);
        _model.Next();

        _facts = _facts with { ReadState = ReadState.Running };
        Assert.False(_model.Settle());
        Assert.Equal(2, View.Number);

        _facts = _facts with { ReadState = ReadState.Succeeded };
        Assert.True(_model.Settle());

        Assert.Equal(3, View.Number);
        Assert.Equal([TutorialRegions.ReadSave], View.Usable);
    }

    [Fact]
    public void A_failed_read_adds_a_note()
    {
        _model.Start(TutorialTopic.Read);
        _model.Next();

        _facts = _facts with { ReadState = ReadState.Failed };
        Assert.True(_model.Settle());

        Assert.Equal(2, View.Number);
        Assert.Equal(UiText.Of(TutorialText.ReadFailedNote), View.Note);

        _facts = _facts with { ReadState = ReadState.Cancelled };
        Assert.False(_model.Settle());
        Assert.Equal(UiText.Of(TutorialText.ReadFailedNote), View.Note);
    }

    [Fact]
    public void Leaving_before_reading_clears_the_box()
    {
        _model.Start(TutorialTopic.Read);
        _facts = _facts with { ReadTabEmpty = false };

        _model.Leave();

        Assert.Equal(1, _readTab.Clears);
        Assert.Equal(0, _readTab.Cancels);
    }

    [Fact]
    public void Leaving_mid_read_cancels_it_and_does_not_clear()
    {
        _model.Start(TutorialTopic.Read);
        _facts = _facts with { ReadTabEmpty = false, LookupSucceeded = true };
        _model.Settle();
        _facts = _facts with { ReadTabEmpty = true, ReadState = ReadState.Running };
        _model.Settle();

        _model.Leave();

        Assert.Equal(1, _readTab.Cancels);
        Assert.Equal(0, _readTab.Clears);
    }

    [Fact]
    public void Leaving_after_a_finished_read_touches_nothing()
    {
        _model.Start(TutorialTopic.Read);
        _model.Next();
        _facts = _facts with { ReadState = ReadState.Running };
        _model.Settle();
        _facts = _facts with { ReadState = ReadState.Succeeded };
        _model.Settle();

        _model.Leave();

        Assert.Equal(0, _readTab.Cancels);
        Assert.Equal(0, _readTab.Clears);
    }

    [Fact]
    public void A_read_that_started_between_settles_is_not_cleared_on_leave()
    {
        _model.Start(TutorialTopic.Read);
        _facts = _facts with { ReadState = ReadState.Succeeded };

        _model.Leave();

        Assert.Equal(0, _readTab.Clears);
        Assert.Equal(0, _readTab.Cancels);
    }

    public static TheoryData<string> UsedTabs() => new() { "typed", "lookup-failed", "succeeded", "saved", "failed" };

    [Theory]
    [MemberData(nameof(UsedTabs))]
    public void Read_on_a_used_tab_is_shown_not_done(string used)
    {
        _facts = used switch
        {
            "typed" => _facts with { ReadTabEmpty = false },
            "lookup-failed" => _facts with { ReadTabEmpty = false, LookupFailed = true },
            "succeeded" => _facts with { ReadState = ReadState.Succeeded },
            "saved" => _facts with { ReadState = ReadState.Succeeded, ReadTabEmpty = false },
            _ => _facts with { ReadState = ReadState.Failed },
        };

        _model.Start(TutorialTopic.Read);

        Assert.Empty(_readTab.Links);
        for (int i = 0; i < 4; i++)
        {
            Assert.Empty(View.Usable);
            Assert.Null(View.Note);
            _model.Settle();
            if (i < 3) _model.Next();
        }

        // Even a lookup that now answers moves nothing: the tab is not the tutorial's.
        _facts = _facts with { LookupSucceeded = true, ReadState = ReadState.Succeeded };
        Assert.Equal(4, View.Number);

        _model.Leave();

        Assert.Equal(0, _readTab.Clears);
        Assert.Equal(0, _readTab.Cancels);
    }

    [Fact]
    public void A_shown_read_topic_does_not_advance_by_itself()
    {
        _facts = _facts with { ReadTabEmpty = false };
        _model.Start(TutorialTopic.Read);

        _facts = _facts with { LookupSucceeded = true };

        Assert.False(_model.Settle());
        Assert.Equal(1, View.Number);
    }

    [Fact]
    public void Changed_is_raised_on_every_move()
    {
        int changes = 0;
        _model.Changed += () => changes++;

        _model.OpenList();
        Assert.Equal(1, changes);

        _model.CloseList();
        Assert.Equal(2, changes);

        _model.OpenList();
        _model.Start(TutorialTopic.AddKey);
        Assert.Equal(4, changes);

        _model.Next();
        Assert.Equal(5, changes);

        _model.Leave();
        Assert.Equal(6, changes);
    }

    [Fact]
    public void Start_resets_the_estimate_of_the_tab_already_showing()
    {
        _shell.Show(AppTab.Publish);
        _nav.ShowEstimate(new PublishRequest(1024, "application/octet-stream", "big.bin", "ref-w", "rWriter", "standalone", SourcePath: "big.bin"));

        _model.Start(TutorialTopic.Publish);

        Assert.Null(_nav.Estimate);
        Assert.Equal(AppTab.Publish, _shell.Tab);
    }

    [Fact]
    public void End_with_a_running_real_read_cancels_it_without_navigating()
    {
        _model.Start(TutorialTopic.Read);
        _facts = _facts with { ReadTabEmpty = false, ReadState = ReadState.Running };

        _model.End();

        Assert.Equal(1, _readTab.Cancels);
        Assert.Equal(0, _readTab.Clears);
        Assert.Equal(AppTab.Read, _shell.Tab);
    }

    [Fact]
    public void End_with_a_real_read_never_started_clears_the_box_without_navigating()
    {
        _shell.Show(AppTab.History);
        _model.Start(TutorialTopic.Read);
        _facts = _facts with { ReadTabEmpty = false };

        _model.End();

        Assert.Equal(1, _readTab.Clears);
        Assert.Equal(0, _readTab.Cancels);
        Assert.Equal(AppTab.Read, _shell.Tab);
    }

    [Fact]
    public void A_leave_during_start_is_not_undone_by_the_sample_link()
    {
        _shell.Changed += () =>
        {
            if (_shell.Tab == AppTab.Read && _model.IsActive) _model.Leave();
        };

        _model.Start(TutorialTopic.Read);

        Assert.False(_model.IsActive);
        Assert.Empty(_readTab.Links);
    }

    [Fact]
    public void Export_keeps_the_row_it_opened_the_menu_for()
    {
        WithKeys();
        _model.Start(TutorialTopic.ExportKey);
        _model.Next();
        Assert.Equal(NotExported, _nav.MenuFor);

        WithKeys(first: "ref-other", notExported: null);
        _model.Next();

        Assert.Equal(NotExported, _nav.ActiveReference);
    }

    [Fact]
    public void Export_latches_the_highlight_with_the_first_row_that_appears()
    {
        _facts = _facts with { KeyCount = 2 };
        _model.Start(TutorialTopic.ExportKey);
        Assert.Equal([TutorialRegions.AccountsRow], View.Lit);

        WithKeys();
        _model.Settle();
        _model.Leave();

        _model.Start(TutorialTopic.ExportKey);
        Assert.Equal([TutorialRegions.AccountsNotExported], View.Lit);
        WithKeys(notExported: null);
        _model.Settle();
        Assert.Equal([TutorialRegions.AccountsNotExported], View.Lit);
    }

    [Fact]
    public void Changed_is_not_raised_while_inside_a_lock()
    {
        bool reentered = false;
        _model.Changed += () =>
        {
            // A handler reading the model from another thread must not deadlock on a held gate.
            Task read = Task.Run(() => _model.IsActive);
            reentered = read.Wait(TimeSpan.FromSeconds(5));
        };

        _model.OpenList();

        Assert.True(reentered);
    }
}
