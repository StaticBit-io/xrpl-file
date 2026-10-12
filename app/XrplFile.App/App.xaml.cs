using XrplFile.App.Core;
using XrplFile.Keys;

namespace XrplFile.App;

public partial class App : Application
{
	/// <summary>
	/// How long a closing window waits for a running publication to stop, and — since part C4 — how
	/// long it then waits for a running read. Bounded on purpose: a run that will not stop must not
	/// hold the window open forever, and when this expires the lock proceeds anyway — which is
	/// exactly C1's behaviour and no worse than it.
	/// <para>
	/// The two waits are sequential, so a window closing over a publication and a read that both
	/// refuse to stop can spend twice this before the vault locks. That is deliberate rather than
	/// overlooked: the stops return the moment their run ends, so the ordinary case — nothing
	/// running, which is most sessions — costs nothing at all, and the worst case needs two hung
	/// runs at once while the window is already going.
	/// </para>
	/// </summary>
	private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

	private readonly IKeyVault _vault;
	private readonly VaultAutoLock _autoLock;
	private readonly PublicationRun _run;
	private readonly ReadRun _read;

	public App(IKeyVault vault, VaultAutoLock autoLock, PublicationRun run, ReadRun read)
	{
		StartupLog.Write("App: constructing");
		InitializeComponent();
		_vault = vault;
		_autoLock = autoLock;
		_run = run;
		_read = read;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		StartupLog.Write("CreateWindow: constructing MainPage");
		MainPage page = new();
		StartupLog.Write("CreateWindow: MainPage constructed");

		Window window = new(page) { Title = "XrplFile.App" };

		// The one teardown hook this application has, and the reason it is wired here rather than
		// left to VaultAutoLock's idle timer to eventually catch up (see MauiProgram's remarks on
		// this decision for what Destroying does and does not cover).
		window.Destroying += OnWindowDestroying;

#if WINDOWS
		AttachClosingConfirmation(window);
#endif

		StartupLog.Write($"CreateWindow: returning; log at {StartupLog.Location}");
		return window;
	}

#if WINDOWS
	/// <summary>
	/// Asks before closing over a running publication. Wired separately from
	/// <see cref="OnWindowDestroying"/> because only WinUI's own <c>AppWindow.Closing</c> can refuse
	/// a close -- <see cref="Window.Destroying"/> is raised once the window is already going and
	/// cannot stop it (design F7). Whether a run is going and what stopping means live in
	/// <see cref="PublicationRun"/>; how long to wait is <see cref="StopTimeout"/>, declared above in
	/// this file. This method only asks the question and, on "yes", stops the run and closes for
	/// real.
	/// </summary>
	private void AttachClosingConfirmation(Window window)
	{
		bool closeApproved = false;
		bool confirming = false;

		window.HandlerChanged += (_, _) =>
		{
			if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window platformWindow) return;

			// No ConfigureAwait(false) anywhere below, unlike OnWindowDestroying: both awaits must
			// resume on the UI thread on purpose -- DisplayAlertAsync needs it to show the dialog,
			// and CloseWindow needs it to close the window.
			platformWindow.AppWindow.Closing += async (_, args) =>
			{
				try
				{
					if (closeApproved) return;

					RunSnapshot snapshot = _run.Snapshot;
					if (snapshot.State != PublicationRunState.Running) return;

					// Cancelled regardless of whether a confirmation is already pending below: a
					// second close attempt while the first is still awaiting an answer must not be
					// allowed through just because nothing here has said "yes" to it yet.
					args.Cancel = true;
					if (confirming) return;

					confirming = true;
					try
					{
						string name = snapshot.Name ?? "the file";
						bool confirmed = window.Page is null || await window.Page.DisplayAlertAsync(
							"Publication in progress",
							$"\"{name}\" is still publishing. Closing now will stop the run; it can be resumed later. Close anyway?",
							"Close",
							"Cancel");

						if (!confirmed) return;

						await _run.StopAsync(StopTimeout);
						closeApproved = true;
						Application.Current?.CloseWindow(window);
					}
					finally
					{
						confirming = false;
					}
				}
				catch (Exception)
				{
					// This runs on WinUI's own Closing event and cannot return a Task, so nothing
					// downstream can observe a fault here -- an escaping exception would just be an
					// unhandled exception on the UI thread. The window is left open on failure: an
					// unclosed window on an unexpected error is safer than one that closed with the
					// run's fate unknown.
				}
			};
		};
	}
#endif

	/// <summary>
	/// Stops a running publication, locks the vault and disposes its idle timer when the window is
	/// going away. See <c>MauiProgram</c>'s remarks on <c>RegisterVault</c> for why this hook, and
	/// what it does not cover.
	/// </summary>
	private async void OnWindowDestroying(object? sender, EventArgs e)
	{
		try
		{
			try
			{
				try
				{
					try
					{
						// Before the lock, not after. Locking first would land while a publication was
						// still going, and the run would die at its next signature with
						// VaultError.Locked -- mid batch, with tickets live and money already spent.
						// Stopping first leaves the state file part A writes, so the run is in the
						// unfinished list next time the application opens.
						await _run.StopAsync(StopTimeout).ConfigureAwait(false);
					}
					finally
					{
						// Runs even if the publication's stop above threw, for the same reason that
						// stop's own guard exists: a read that outlives the window is no better than a
						// publication that does, and its own StopAsync deletes the temporary file it
						// was writing to as part of stopping -- nothing else here is needed for that.
						await _read.StopAsync(StopTimeout).ConfigureAwait(false);
					}
				}
				finally
				{
					// Runs even if either stop above threw -- cancelling a token propagates whatever a
					// registered callback throws, and a data key that outlives a failed stop with
					// the idle timer already disposed (below) would have nothing left to reclaim it.
					// The lock belongs on every path out of this handler, not only the one where the
					// stop succeeded.
					await _vault.LockAsync().ConfigureAwait(false);
				}
			}
			finally
			{
				// Disposed even if the lock above somehow threw: an idle timer left ticking after
				// the window it protected is gone is its own, separate leak.
				await _autoLock.DisposeAsync().ConfigureAwait(false);
			}
		}
		catch (Exception)
		{
			// The window is already on its way out; there is nothing left here to report a
			// teardown failure to, and letting an exception escape an event handler during
			// shutdown would be the worse failure than a resident key this process is about to
			// tear down anyway.
		}
	}
}
