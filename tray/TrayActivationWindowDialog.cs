using System.Globalization;
using System.Drawing;
using System.Windows.Forms;

namespace CodexContinuity.Tray;

internal sealed record TrayActivationWindowSelection(
    string Range,
    string TimeZoneId,
    bool IsOvernight);

internal static class TrayActivationWindowPlanner
{
    private const string TimeFormat = "HH:mm";

    internal static bool TryCreate(
        string startText,
        string endText,
        string timeZoneId,
        out TrayActivationWindowSelection? selection,
        out string error)
    {
        selection = null;
        if (!TimeOnly.TryParseExact(
                startText,
                TimeFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var start) ||
            !TimeOnly.TryParseExact(
                endText,
                TimeFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var end))
        {
            error = "Enter both times as HH:mm.";
            return false;
        }

        if (start == end)
        {
            error = "Start and end must be different.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId.Length > 128)
        {
            error = "Choose a valid local time zone.";
            return false;
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            error = "The selected time zone is not available on this computer.";
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            error = "The selected time zone is invalid.";
            return false;
        }
        catch (ArgumentException)
        {
            error = "The selected time zone is invalid.";
            return false;
        }

        selection = new(
            $"{start.ToString(TimeFormat, CultureInfo.InvariantCulture)}-" +
                $"{end.ToString(TimeFormat, CultureInfo.InvariantCulture)}",
            timeZoneId,
            end < start);
        error = string.Empty;
        return true;
    }

    internal static bool TryCreateRange(
        string range,
        string timeZoneId,
        out TrayActivationWindowSelection? selection,
        out string error)
    {
        selection = null;
        if (string.IsNullOrWhiteSpace(range))
        {
            error = "The activation window is required.";
            return false;
        }

        var parts = range.Split('-', StringSplitOptions.None);
        return parts.Length == 2
            ? TryCreate(parts[0], parts[1], timeZoneId, out selection, out error)
            : Fail("The activation window must use HH:mm-HH:mm.", out error);
    }

    internal static IReadOnlyList<string> BuildArguments(
        TrayActivationWindowSelection selection) =>
        [
            "update-policy",
            "--activation-window",
            selection.Range,
            "--time-zone",
            selection.TimeZoneId,
        ];

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}

internal sealed class TrayActivationWindowDialog : Form
{
    private readonly DateTimePicker startPicker = new();
    private readonly DateTimePicker endPicker = new();
    private readonly TextBox timeZoneTextBox = new();
    private readonly ErrorProvider errorProvider = new();

    internal TrayActivationWindowSelection? Selection { get; private set; }

    internal static TrayActivationWindowSelection? AcceptedSelection(
        DialogResult result,
        TrayActivationWindowSelection? selection) =>
        result == DialogResult.OK ? selection : null;

    internal TrayActivationWindowDialog(string? timeZoneId = null)
    {
        var localTimeZoneId = string.IsNullOrWhiteSpace(timeZoneId)
            ? TimeZoneInfo.Local.Id
            : timeZoneId;

        AccessibleRole = AccessibleRole.Dialog;
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Custom activation window";
        ClientSize = new Size(440, 255);

        var explanation = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text =
                "Choose local clock times when safe activation is allowed. " +
                "If the end is earlier than the start, the window runs overnight " +
                "(for example, 22:00-07:00). Start and end must differ.",
            AccessibleName = "Activation window explanation",
            TabIndex = 0,
        };

        ConfigureTimePicker(startPicker, "Start time");
        ConfigureTimePicker(endPicker, "End time");
        SetPickerToLocalTime(startPicker, DateTime.Now);
        SetPickerToLocalTime(endPicker, DateTime.Now.AddHours(1));

        timeZoneTextBox.ReadOnly = true;
        timeZoneTextBox.Text = localTimeZoneId;
        timeZoneTextBox.Dock = DockStyle.Fill;
        timeZoneTextBox.AccessibleName = "Local time zone";
        timeZoneTextBox.TabIndex = 3;

        var startLabel = new Label
        {
            Text = "Start:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            AccessibleName = "Start time label",
        };
        var endLabel = new Label
        {
            Text = "End:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            AccessibleName = "End time label",
        };
        var timeZoneLabel = new Label
        {
            Text = "Time zone:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            AccessibleName = "Time zone label",
        };

        var fields = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 3,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.Controls.Add(startLabel, 0, 0);
        fields.Controls.Add(startPicker, 1, 0);
        fields.Controls.Add(endLabel, 0, 1);
        fields.Controls.Add(endPicker, 1, 1);
        fields.Controls.Add(timeZoneLabel, 0, 2);
        fields.Controls.Add(timeZoneTextBox, 1, 2);

        var applyButton = new Button
        {
            Text = "Apply",
            AutoSize = true,
            DialogResult = DialogResult.None,
            AccessibleName = "Apply custom activation window",
            TabIndex = 4,
        };
        applyButton.Click += (_, _) => AcceptSelection();
        var cancelButton = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            AccessibleName = "Cancel custom activation window",
            TabIndex = 5,
        };

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
        };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(applyButton);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 4,
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(explanation, 0, 0);
        layout.Controls.Add(fields, 0, 1);
        layout.Controls.Add(new Panel(), 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);

        AcceptButton = applyButton;
        CancelButton = cancelButton;
        errorProvider.BlinkStyle = ErrorBlinkStyle.NeverBlink;
    }

    private static void ConfigureTimePicker(DateTimePicker picker, string accessibleName)
    {
        picker.Format = DateTimePickerFormat.Custom;
        picker.CustomFormat = "HH:mm";
        picker.ShowUpDown = true;
        picker.Width = 90;
        picker.AccessibleName = accessibleName;
        picker.TabIndex = accessibleName.StartsWith("Start", StringComparison.Ordinal)
            ? 1
            : 2;
    }

    private static void SetPickerToLocalTime(DateTimePicker picker, DateTime value)
    {
        picker.Value = DateTime.Today.AddHours(value.Hour).AddMinutes(value.Minute);
    }

    private void AcceptSelection()
    {
        var start = startPicker.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
        var end = endPicker.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (!TrayActivationWindowPlanner.TryCreate(
                start,
                end,
                timeZoneTextBox.Text,
                out var selection,
                out var error))
        {
            errorProvider.SetError(startPicker, error);
            errorProvider.SetError(endPicker, error);
            return;
        }

        errorProvider.Clear();
        Selection = selection;
        DialogResult = DialogResult.OK;
        Close();
    }
}
