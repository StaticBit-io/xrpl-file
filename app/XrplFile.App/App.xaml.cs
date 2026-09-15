using XrplFile.App.Core;
using XrplFile.Keys;

namespace XrplFile.App;

public partial class App : Application
{
	/// <summary>
	/// How long a closing window waits for a running publication to stop. Bounded on purpose: a run
	/// that will not stop must not hold the window open forever, and when this expires the lock
	/// proceeds anyway — which is exactly C1's behaviour and no worse than it.
	/// </summary>
	private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

	private readonly IKeyVault _vault;
	private readonly VaultAutoLock _autoLock;
	private readonly PublicationRun _run;

	public App(IKeyVault vault, VaultAutoLock autoLock, PublicationRun run)
	{
		InitializeComponent();
		_vault = vault;
		_autoLock = autoLock;
		_run = run;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		Window window = new(new MainPage()) { Title = "XrplFile.App" };

		// The one teardown hook this application has, and the reason it is wired here rather than
		// left to VaultAutoLock's idle timer to eventually catch up (see MauiProgram's remarks on
		// this decision for what Destroying does and does not cover).
		window.Destroying += OnWindowDestroying;

#if WINDOWS
		AttachClosingConfirmation(window);
#endif

		return window;
	}

#if WINDOWS
	/// <summary>
	/// Asks before closing over a running publication. Wired separately from
	/// <see cref="OnWindowDestroying"/> because only WinUI's own <c>AppWindow.Closing</c> can refuse
	/// a close -- <see cref="Window.Destroying"/> is raised once the window is already going and
	/// cannot stop it (design F7). Everything this decides -- whether a run is going, what stopping
	/// means, how long to wait -- lives in <see cref="PublicationRun"/>; this only asks the question
	/// and, on "yes", stops the run and closes for real.
	/// </summary>
	private void AttachClosingConfirmation(Window window)
	{
		bool closeApproved = false;

		window.HandlerChanged += (_, _) =>
		{
			if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window platformWindow) return;

			platformWindow.AppWindow.Closing += async (_, args) =>
			{
				if (closeApproved || _run.Snapshot.State != PublicationRunState.Running) return;

				args.Cancel = true;

				try
				{
					string name = _run.Snapshot.Name ?? "the file";
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
				// Before the lock, not after. Locking first would land while a publication was
				// still going, and the run would die at its next signature with
				// VaultError.Locked -- mid batch, with tickets live and money already spent.
				// Stopping first leaves the state file part A writes, so the run is in the
				// unfinished list next time the application opens.
				await _run.StopAsync(StopTimeout).ConfigureAwait(false);
				await _vault.LockAsync().ConfigureAwait(false);
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
