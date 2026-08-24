using System.Drawing;

namespace CodexContinuity.Tray;

internal sealed class TrayMenuController
{
    private bool visible;

    internal bool IsVisible => visible;

    internal void Toggle(Action show, Action close)
    {
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(close);
        if (visible)
        {
            visible = false;
            close();
            return;
        }
        show();
        visible = true;
    }

    internal void MarkClosed() => visible = false;

    internal void Close(Action close)
    {
        ArgumentNullException.ThrowIfNull(close);
        if (!visible)
        {
            return;
        }
        visible = false;
        close();
    }
}

internal sealed class TrayMenuHost : IDisposable
{
    private readonly ContextMenuStrip menu;
    private readonly MenuOwnerWindow owner;
    private readonly TrayMenuController controller = new();
    private bool disposed;

    internal TrayMenuHost(ContextMenuStrip menu)
    {
        this.menu = menu ?? throw new ArgumentNullException(nameof(menu));
        this.menu.AutoClose = true;
        this.menu.Closed += HandleClosed;
        this.menu.KeyDown += HandleKeyDown;
        owner = new MenuOwnerWindow();
        owner.CreateControl();
    }

    internal bool IsVisible => controller.IsVisible || menu.Visible;

    internal IWin32Window Owner => owner;

    internal void Toggle(Point screenPosition)
    {
        ThrowIfDisposed();
        controller.Toggle(
            () =>
            {
                owner.Location = screenPosition;
                menu.Show(owner, new Point(0, 0));
            },
            () => menu.Close(ToolStripDropDownCloseReason.AppClicked));
    }

    internal void Close()
    {
        ThrowIfDisposed();
        menu.Close(ToolStripDropDownCloseReason.AppClicked);
        controller.MarkClosed();
    }

    private void HandleClosed(object? sender, ToolStripDropDownClosedEventArgs eventArgs) =>
        controller.MarkClosed();

    private void HandleKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode == Keys.Escape)
        {
            Close();
            eventArgs.Handled = true;
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(TrayMenuHost));
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        menu.Closed -= HandleClosed;
        menu.KeyDown -= HandleKeyDown;
        menu.Close(ToolStripDropDownCloseReason.CloseCalled);
        owner.Dispose();
    }

    private sealed class MenuOwnerWindow : Form
    {
        internal MenuOwnerWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(1, 1);
            Opacity = 0;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                return parameters;
            }
        }
    }
}
