using XrplFile.Keys;

namespace XrplFile.App;

public partial class App : Application
{
	private readonly IKeyVault _vault;
	private readonly VaultAutoLock _autoLock;

	public App(IKeyVault vault, VaultAutoLock autoLock)
	{
		InitializeComponent();
		_vault = vault;
		_autoLock = autoLock;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		Window window = new(new MainPage()) { Title = "XrplFile.App" };

		// The one teardown hook this application has, and the reason it is wired here rather than
		// left to VaultAutoLock's idle timer to eventually catch up (see MauiProgram's remarks on
		// this decision for what Destroying does and does not cover).
		window.Destroying += OnWindowDestroying;

		return window;
	}

	/// <summary>
	/// Locks the vault and disposes its idle timer when the window is going away. See
	/// <c>MauiProgram</c>'s remarks on <c>RegisterVault</c> for why this hook, and what it does
	/// not cover.
	/// </summary>
	private async void OnWindowDestroying(object? sender, EventArgs e)
	{
		try
		{
			try
			{
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
