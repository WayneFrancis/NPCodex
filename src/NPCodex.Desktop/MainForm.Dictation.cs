using System.ComponentModel;
using System.Globalization;
using System.Speech.Recognition;
using System.Windows.Forms;

namespace NPCodex.Desktop;

/// <summary>
/// Right-click dictation for standard WinForms text boxes.
/// Only one microphone/recognizer session is active across the form.
/// Recognized text is draft text: it is never auto-approved or saved.
/// </summary>
public sealed partial class MainForm
{
    private readonly HashSet<TextBoxBase> _dictationFields = [];
    private SpeechRecognitionEngine? _dictationEngine;
    private TextBoxBase? _dictationTarget;
    private bool _dictationStopping;

    private void AttachDictationToTextBoxes(Control root)
    {
        if (root is TextBoxBase box && _dictationFields.Add(box))
        {
            // Retain existing menu if present; otherwise provide familiar
            // editing commands alongside the start/stop dictation actions.
            var menu = box.ContextMenuStrip;
            if (menu is null)
            {
                menu = new ContextMenuStrip();
                var cut = new ToolStripMenuItem("Cut");
                cut.Click += (_, _) => { if (!box.ReadOnly) box.Cut(); };
                var copy = new ToolStripMenuItem("Copy");
                copy.Click += (_, _) => box.Copy();
                var paste = new ToolStripMenuItem("Paste");
                paste.Click += (_, _) => { if (!box.ReadOnly) box.Paste(); };
                var select = new ToolStripMenuItem("Select All");
                select.Click += (_, _) => box.SelectAll();
                menu.Items.AddRange([cut, copy, paste, select]);
                box.ContextMenuStrip = menu;
            }

            menu.Items.Add(new ToolStripSeparator());
            var start = new ToolStripMenuItem("Start dictation (microphone)");
            var stop = new ToolStripMenuItem("Stop dictation");
            var diagnose = new ToolStripMenuItem("Speech recognizer diagnostics...");
            var voiceTyping = new ToolStripMenuItem("Windows voice typing (Win+H)...");
            start.Click += (_, _) => StartDictation(box);
            stop.Click += (_, _) => StopDictation();
            diagnose.Click += (_, _) => ShowSpeechDiagnostics();
            voiceTyping.Click += (_, _) =>
            {
                box.Focus();
                _status.Text = "Press Windows+H to dictate with Windows 11 " +
                               "voice typing. NPCodex will not start it automatically.";
                MessageBox.Show(this,
                    "Focus is now on the text box. Press Windows + H " +
                    "to start Windows voice typing, and stop it using " +
                    "the Windows voice typing toolbar.",
                    "Windows voice typing", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                box.Focus();
            };
            menu.Items.Add(start);
            menu.Items.Add(stop);
            menu.Items.Add(diagnose);
            menu.Items.Add(voiceTyping);
            menu.Opening += (_, _) =>
            {
                var active = _dictationEngine is not null;
                start.Visible = !active;
                start.Enabled = !box.ReadOnly && box.Enabled && !_dictationStopping;
                stop.Visible = active;
                stop.Enabled = !_dictationStopping;
            };
            box.Disposed += (_, _) =>
            {
                _dictationFields.Remove(box);
                if (ReferenceEquals(box, _dictationTarget))
                    StopDictation();
            };
        }

        foreach (Control child in root.Controls)
            AttachDictationToTextBoxes(child);
    }

    private void StartDictation(TextBoxBase target)
    {
        if (_dictationEngine is not null || _dictationStopping ||
            target.ReadOnly || target.IsDisposed)
            return;

        SpeechRecognitionEngine? engine = null;
        try
        {
            // Prefer the current Windows language if it has a recognizer,
            // otherwise an installed recognizer for the same language.
            var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
            var locale = CultureInfo.CurrentUICulture;
            var selected = recognizers.FirstOrDefault(info =>
                info.Culture.Equals(locale))
                ?? recognizers.FirstOrDefault(info =>
                    info.Culture.TwoLetterISOLanguageName ==
                    locale.TwoLetterISOLanguageName)
                ?? recognizers.FirstOrDefault();
            if (selected is null)
                throw new InvalidOperationException(
                    "Windows has no installed compatible legacy speech recognizer. " +
                    "Install a speech recognition language for Windows, or use " +
                    "Windows voice typing with Win+H as an alternative.");

            engine = new SpeechRecognitionEngine(selected);
            engine.InitialSilenceTimeout = TimeSpan.FromSeconds(30);
            engine.BabbleTimeout = TimeSpan.FromSeconds(10);
            engine.LoadGrammar(new DictationGrammar());
            engine.SetInputToDefaultAudioDevice();
            var activeEngine = engine;
            engine.SpeechDetected += (_, _) => ReportDictationStatus(
                activeEngine, "Speech detected. Recognizing words...");
            engine.SpeechRecognitionRejected += (_, e) =>
            {
                var detail = e.Result is { Text.Length: > 0 }
                    ? $"Heard '{e.Result.Text}', but the recognizer rejected it."
                    : "Speech heard, but no words matched the recognition grammar.";
                ReportDictationStatus(activeEngine, detail);
            };
            engine.AudioSignalProblemOccurred += (_, e) =>
                ReportDictationStatus(activeEngine,
                    "Microphone audio problem: " + e.AudioSignalProblem);
            engine.AudioStateChanged += (_, e) =>
            {
                if (e.AudioState == AudioState.Stopped)
                    ReportDictationStatus(activeEngine,
                        "Speech engine stopped receiving microphone audio.");
            };
            engine.SpeechRecognized += (_, e) =>
            {
                if (e.Result is null || string.IsNullOrWhiteSpace(e.Result.Text))
                    return;
                if (e.Result.Confidence < 0.2f)
                {
                    ReportDictationStatus(activeEngine,
                        $"Recognition confidence low ({e.Result.Confidence:P0}); ignored.");
                    return;
                }
                var phrase = e.Result.Text;
                if (!IsDisposed && IsHandleCreated)
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            if (_dictationEngine != activeEngine ||
                                _dictationStopping || target.IsDisposed ||
                                target.ReadOnly) return;
                            InsertDictatedText(target, phrase);
                            _status.Text = $"Dictated: {phrase}  " +
                                "(right-click a field to stop)";
                        }));
                    }
                    catch (InvalidOperationException)
                    {
                        // Form is shutting down. Never modify a disposed field.
                    }
                }
            };
            engine.RecognizeCompleted += (_, e) =>
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            var unexpectedError = e.Error;
                            FinishDictation(activeEngine);
                            if (unexpectedError is not null && !IsDisposed)
                                _status.Text =
                                    "Dictation stopped: " + unexpectedError.Message;
                        }));
                    }
                    catch (InvalidOperationException)
                    {
                        // The form may have closed before completion arrived.
                    }
                }
            };

            _dictationTarget = target;
            _dictationEngine = engine;
            _dictationStopping = false;
            engine.RecognizeAsync(RecognizeMode.Multiple);
            target.Focus();
            _status.Text = $"Listening via {selected.Name} " +
                           $"({selected.Culture.Name}). Speak a short phrase. " +
                           "Status will report speech detection or audio errors.";
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_dictationEngine, engine))
                _dictationEngine = null;
            _dictationTarget = null;
            _dictationStopping = false;
            engine?.Dispose();
            MessageBox.Show(
                "Could not start dictation.\n\n" + ex.Message +
                "\n\nCheck the default microphone, Windows microphone " +
                "privacy settings, and installed recognition language. " +
                "Windows voice typing (Win+H) is another option.",
                "Dictation unavailable", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    private void ReportDictationStatus(
        SpeechRecognitionEngine engine, string message)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (ReferenceEquals(_dictationEngine, engine) &&
                    !_dictationStopping && !IsDisposed)
                    _status.Text = message;
            }));
        }
        catch (InvalidOperationException)
        {
            // UI is closing while a background recognizer event finishes.
        }
    }

    private void ShowSpeechDiagnostics()
    {
        try
        {
            var installed = SpeechRecognitionEngine.InstalledRecognizers();
            var current = CultureInfo.CurrentUICulture;
            var description = installed.Count == 0
                ? "No compatible System.Speech recognizers installed. " +
                  "Sound Recorder working does not imply a legacy speech " +
                  "recognizer is available in Windows 11."
                : string.Join(Environment.NewLine,
                    installed.Select(r => $"{r.Name} — {r.Culture.Name}"));
            MessageBox.Show(this,
                $"Windows UI language: {current.Name}\\n" +
                $"Installed System.Speech recognizers: {installed.Count}\\n" +
                description +
                "\\n\\nWindows 11 voice typing is separate from System.Speech. " +
                "To test that alternative, click in a text box and press Win+H.",
                "Speech recognition diagnostics", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(),
                "Speech recognizer diagnostics failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void InsertDictatedText(TextBoxBase target, string phrase)
    {
        if (target.IsDisposed || phrase.Length == 0) return;
        // Insert at the caret; don't erase what was already written.
        var position = target.SelectionStart;
        var preceding = position > 0 ? target.Text[position - 1] : (char?)null;
        var prefix = preceding is null || char.IsWhiteSpace(preceding.Value) ||
                     preceding.Value is '(' or '[' or '\n' ? "" : " ";
        target.SelectedText = prefix + phrase + " ";
    }

    private void StopDictation()
    {
        if (_dictationEngine is not { } engine || _dictationStopping) return;
        _dictationStopping = true;
        _status.Text = "Stopping microphone dictation...";
        try
        {
            // Cancel immediately; RecognizeCompleted handles final disposal.
            engine.RecognizeAsyncCancel();
        }
        catch (Exception)
        {
            FinishDictation(engine);
        }
    }

    private void FinishDictation(SpeechRecognitionEngine engine)
    {
        if (!ReferenceEquals(engine, _dictationEngine)) return;
        _dictationEngine = null;
        _dictationTarget = null;
        _dictationStopping = false;
        engine.Dispose();
        if (!IsDisposed)
            _status.Text = "Dictation stopped. Text remains editable and unsaved.";
    }

    private void DisposeDictation()
    {
        var engine = _dictationEngine;
        _dictationEngine = null;
        _dictationTarget = null;
        _dictationStopping = false;
        if (engine is null) return;
        try { engine.RecognizeAsyncCancel(); }
        catch (Exception) { /* Already stopped. */ }
        engine.Dispose();
    }
}
