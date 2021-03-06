﻿#region License
// CShell, A Simple C# Scripting IDE
// Copyright (C) 2013  Arnova Asset Management Ltd., Lukas Buhler
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.
#endregion
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.VisualStyles;
using System.Windows.Input;
using System.Windows.Media;
using CShell.Code;
using CShell.Framework.Services;
using CShell.Util;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.NRefactory.Editor;

namespace CShell.Modules.Repl.Controls
{
    /// <summary>
    /// Interaction logic for CommandLineControl.xaml
    /// </summary>
    public partial class CSRepl : UserControl, IRepl
    {
        private CSReplTextEditor textEditor;

        private ScriptingEngine scriptingEngine;
        private readonly CommandHistory commandHistory;

        private bool executingInternalCommand;
        private string partialCommand = "";
        private int evaluationsRunning;
        private IVisualLineTransformer[] initialTransformers;

        CompletionWindow completionWindow;
        OverloadInsightWindow insightWindow;

        public CSRepl()
        {
            InitializeComponent();

            textEditor = new CSReplTextEditor();
            textEditor.FontFamily = new FontFamily("Consolas");
            var convertFrom = new FontSizeConverter().ConvertFrom("10pt");
            if (convertFrom != null) textEditor.FontSize = (double)convertFrom;
            textEditor.TextArea.PreviewKeyDown += TextAreaOnPreviewKeyDown;
            textEditor.IsEnabled = false;
            textEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
            textEditor.Document.FileName = "repl.csx";
            textEditor.Repl = this;
            this.Content = textEditor;

            commandHistory = new CommandHistory();

            ShowConsoleOutput = true;
            OutputColor = Color.FromArgb(255, 78, 78, 78);
            WarningColor = Color.FromArgb(255, 183, 122, 0);
            ErrorColor = Color.FromArgb(255, 138, 6, 3);
            ReplColor = Color.FromArgb(255, 0, 127, 0);

            //supress duplicate using warnings
            SuppressWarning("CS0105");

            //clears the console and prints the headers
            // when clearing the initial transormers are removed too but we want to keep them
            initialTransformers = textEditor.TextArea.TextView.LineTransformers.ToArray();
            Clear();
        }

        #region IRepl Interface Implementation
        public ScriptingEngine ScriptingEngine
        {
            get { return scriptingEngine; }
            set
            {
                if (scriptingEngine != null)
                {
                    scriptingEngine.ConsoleOutput -= ScriptingEngineOnConsoleOutput;
                    scriptingEngine.EvaluateStarted -= ScriptingEngineOnEvaluateStarted;
                    scriptingEngine.EvaluateCompleted -= ScriptingEngineOnEvaluateCompleted;
                    textEditor.Completion = null;
                }
                scriptingEngine = value;
                if (scriptingEngine != null)
                {
                    scriptingEngine.ConsoleOutput += ScriptingEngineOnConsoleOutput;
                    scriptingEngine.EvaluateStarted += ScriptingEngineOnEvaluateStarted;
                    scriptingEngine.EvaluateCompleted += ScriptingEngineOnEvaluateCompleted;
                    textEditor.Completion = scriptingEngine.CodeCompletion;
                }
                textEditor.IsEnabled = scriptingEngine != null;

                Clear();
            }
        }

        public bool IsEvaluating
        {
            get { return evaluationsRunning > 0; }
        }

        public string Font
        {
            get { return textEditor.FontFamily.ToString(); }
            set { textEditor.FontFamily = new FontFamily(value); }
        }

        public new double FontSize
        {
            get { return textEditor.FontSize; }
            set { textEditor.FontSize = value; }
        }

        public Color BackgroundColor
        {
            get
            {
                var b = textEditor.Background as SolidColorBrush;
                if (b != null) return b.Color;
                else
                    return Colors.Black;
            }
            set { textEditor.Background = new SolidColorBrush(value); }
        }

        public Color OutputColor { get; set; }
        public Color WarningColor { get; set; }
        public Color ErrorColor { get; set; }
        public Color ReplColor { get; set; }

        private readonly HashSet<string> suppressedWarnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public IEnumerable<string> SuppressedWarnings
        {
            get { return suppressedWarnings; }
        }

        public void SuppressWarning(string warningCode)
        {
            if(string.IsNullOrEmpty(warningCode))
                return;
            if (!suppressedWarnings.Contains(warningCode))
                suppressedWarnings.Add(warningCode);
        }
        public void ShowWarning(string warningCode)
        {
            if (string.IsNullOrEmpty(warningCode))
                return;
            if (suppressedWarnings.Contains(warningCode))
                suppressedWarnings.Remove(warningCode);
        }

        private IEnumerable<string> FilterWarnings(IEnumerable<string> warnings)
        {
            return warnings.Where(warning => !suppressedWarnings.Contains(GetWarningCode(warning)));
        }

        private string GetWarningCode(string warning)
        {
            var warningStart = warning.IndexOf("warning", StringComparison.OrdinalIgnoreCase);
            if (warningStart >= 0)
            {
                var start = warningStart + "warning".Length;
                var warningEnd = warning.IndexOf(':', start);
                if (warningEnd > 0 && warningEnd > start)
                {
                    var code = warning.Substring(start, warningEnd - start);
                    return code.Trim();
                }
            }
            return "";
        }

        public bool ShowConsoleOutput { get; set; }

        public void Clear()
        {
            textEditor.Text = String.Empty;
            //so that the previous code highligting is cleared too
            textEditor.TextArea.TextView.LineTransformers.Clear();
            foreach (var visualLineTransformer in initialTransformers)
                textEditor.TextArea.TextView.LineTransformers.Add(visualLineTransformer);


            WriteLine("CShell REPL (" + Assembly.GetExecutingAssembly().GetName().Version + ")", TextType.Repl);

            if (scriptingEngine != null)
            {
                WriteLine("Enter C# code to be evaluated or enter \"help\" for more information.", TextType.Repl);
                WritePrompt();
            }
            else
            {
                WriteLine("No workspace open, open a workspace to use the REPL.", TextType.Warning);
            }

        }
        #endregion

        #region Handle REPL Input
        private void CommandEntered(string command)
        {
            Debug.WriteLine("Command: " + command);

            if(scriptingEngine == null)
                throw new InvalidOperationException("Scripting engine cannot be null when entering commands.");
            WriteLine();
            commandHistory.Add(command);
            executingInternalCommand = true;
            evaluationsRunning++;
            var input = partialCommand + Environment.NewLine + command;
            input = input.Trim();
            scriptingEngine.EvaluateAsync(input);
        }

        private void ShowPreviousCommand()
        {
            if(commandHistory.DoesPreviousCommandExist())
            {
                ClearLine();
                Write(commandHistory.GetPreviousCommand(), TextType.None);
            }
        }

        private void ShowNextCommand()
        {
            if (commandHistory.DoesNextCommandExist())
            {
                ClearLine();
                Write(commandHistory.GetNextCommand(), TextType.None);
            }
        }
        #endregion

        #region ScriptingEngine Events
        private void ScriptingEngineOnEvaluateStarted(object sender, EvaluateStartedEventArgs evaluateStartedEventArgs)
        {
            if (!executingInternalCommand)
            {
                Execute.OnUIThread(() =>
                {
                    if (!IsEvaluating)
                    {
                        ClearLine();
                        WriteLine();
                    }
                    evaluationsRunning++;
                    var source = evaluateStartedEventArgs.SourceFile != null ? System.IO.Path.GetFileName(evaluateStartedEventArgs.SourceFile): "unknown source";
                    WriteLine("[Evaluating external code (" + source + ")]", TextType.Repl);
                });
            }
        }

        private void ScriptingEngineOnEvaluateCompleted(object sender, EvaluateCompletedEventArgs evaluateCompletedEventArgs)
        {
            Execute.OnUIThread(() =>
            {
                var result = evaluateCompletedEventArgs.Resuslt;
                if (!result.InputComplete)
                {
                    partialCommand = result.Input;
                    prompt = promptIncomplete;
                }
                else
                {
                    partialCommand = "";
                    prompt = promptComplete;
                }

                if (result.HasErrors)
                    WriteLine(String.Join(Environment.NewLine, result.Errors), TextType.Error);
                if (result.HasWarnings)
                {
                    var warnings = FilterWarnings(result.Warnings).ToList();
                    if(warnings.Any())
                        WriteLine(String.Join(Environment.NewLine, warnings), TextType.Warning);
                }

                if (result.HasResult && result.Result != null)
                    WriteLine(ToPrettyString(result.Result));

                executingInternalCommand = false;
                evaluationsRunning--;
                if (!IsEvaluating)
                {
                    if (ScriptingInteractiveBase.ClearRequested)
                    {
                        Clear();
                        ScriptingInteractiveBase.ClearRequested = false;
                    }
                    else
                        WritePrompt();
                }
            });
        }

        private void ScriptingEngineOnConsoleOutput(object sender, ConsoleEventArgs eventArgs)
        {
            if(ShowConsoleOutput)
                Execute.OnUIThread(() => Write(eventArgs.Text, TextType.Output));
        }

        private string ToPrettyString(object o)
        {
            if (o is String)
                return o.ToString();

            var enumerable = o as IEnumerable;
            if(enumerable != null)
            {
                var items = enumerable.Cast<object>().Take(21).ToList();
                var firstItems = items.Take(20).ToList();
                var sb = new StringBuilder();
                sb.Append("{");
                sb.Append(String.Join(", ", firstItems));
                if (items.Count > firstItems.Count)
                    sb.Append("...");
                sb.Append("}");
                return sb.ToString();
            }
            return o.ToString();
        }
        #endregion

        #region TextEditor Events
        private void TextAreaOnPreviewKeyDown(object sender, KeyEventArgs keyEventArgs)
        {
            //Debug.WriteLine("TextArea PreviewKeyDown: " + keyEventArgs.Key);
            var key = keyEventArgs.Key;

            //allow copy or interrup
            if ((Keyboard.IsKeyDown(Key.LeftCtrl) && Keyboard.IsKeyDown(Key.C)) ||
                (Keyboard.IsKeyDown(Key.RightCtrl) && Keyboard.IsKeyDown(Key.C)))
            {
                if (IsEvaluating)
                {
                    WriteLine("[Interrupting]", TextType.Repl);
                    ScriptingEngine.Interrupt();
                }
                return;
            }

            if(IsEvaluating)
            {
                WriteLine("[Evaluation is running, press 'ctrl+c' to interrupt]", TextType.Repl);
                keyEventArgs.Handled = true;
                return;
            }

            if(key == Key.Enter)
            {
                var command = GetCurrentLineText();
                CommandEntered(command);
                keyEventArgs.Handled = true;
                return;
            }
            if(key == Key.Up)
            {
                ShowPreviousCommand();
                keyEventArgs.Handled = true;
                return;
            }
            if (key == Key.Down)
            {
                ShowNextCommand();
                keyEventArgs.Handled = true;
                return;
            }
            if (key == Key.Back || key == Key.Left)
            {
                if(IsCaretAtPrompt())
                {
                    keyEventArgs.Handled = true;
                    return;
                }
            }
            if(key == Key.Home || key == Key.End)
            {
                MoveCaretToEnd();
                keyEventArgs.Handled = true;
                return;
            }

            if (!IsCaretAtWritablePosition())
            {
                keyEventArgs.Handled = true;
            }
            else
            {
                //it's possible to select more than the current line with the mouse, but the cursor ends at the current line
                // in this case when the selection is edited, it needs to be changed only to the current line.
                if (IsSelectionBeforePrompOffset())
                    SelectCurrentLineOnly();

                //if Crtl+x is pressed and no text is selected we need to select only the text after the prompt
                if ((Keyboard.IsKeyDown(Key.LeftCtrl) && Keyboard.IsKeyDown(Key.X)) ||
                    (Keyboard.IsKeyDown(Key.RightCtrl) && Keyboard.IsKeyDown(Key.X)))
                {
                    if (textEditor.SelectionLength == 0)
                        SelectCurrentLine();
                    if (IsCaretAtPrompt())
                        keyEventArgs.Handled = true;
                }
            }
        }

        internal IDocument GetCompletionDocument(out int offset)
        {
            var lineText = GetCurrentLineText();
            var line = Doc.GetLineByOffset(Offset);
            offset = Offset - line.Offset - prompt.Length;

            var vars = ScriptingEngine.GetVars();
            var code = vars + lineText;
            offset += vars.Length;
            var doc = new ReadOnlyDocument(new StringTextSource(code), textEditor.Document.FileName);
            return doc;
        }
        #endregion

        #region TextEditor Helpers
        private string promptComplete = " > ";
        private string promptIncomplete = "   ";
        private string prompt = " > ";
        private TextDocument Doc
        {
            get { return textEditor.Document; }
        }

        private int Offset
        {
            get { return textEditor.CaretOffset; }
        }

        private int PromptOffset
        {
            get
            {
                var lastLine = Doc.GetLineByNumber(Doc.LineCount);
                return lastLine.Offset + prompt.Length;
            }
        }

        private void MoveCaretToEnd()
        {
            textEditor.CaretOffset = Doc.TextLength;
            textEditor.ScrollToEnd(); 
        }

        private bool IsCaretAtCurrentLine()
        {
            var offsetLine = Doc.GetLocation(Offset).Line;
            return offsetLine == Doc.LineCount;
        }

        private bool IsCaretAfterPrompt()
        {
            var offsetColumn = Doc.GetLocation(Offset).Column;
            return offsetColumn > prompt.Length;
        }

        private bool IsCaretAtPrompt()
        {
            var offsetColumn = Doc.GetLocation(Offset).Column;
            return offsetColumn-1 == prompt.Length;
        }

        private bool IsCaretAtWritablePosition()
        {
            return IsCaretAtCurrentLine() && IsCaretAfterPrompt();
        }

        private bool IsSelectionBeforePrompOffset()
        {
            return textEditor.SelectionLength > 0 && textEditor.SelectionStart < PromptOffset;
        }

        private void SelectCurrentLineOnly()
        {
            var oldSelectionStart = textEditor.SelectionStart;
            var oldSelectionEnd = oldSelectionStart + textEditor.SelectionLength;
            textEditor.SelectionLength = 0;
            textEditor.SelectionStart = PromptOffset;
            textEditor.SelectionLength = oldSelectionEnd - PromptOffset;
        }

        private void SelectCurrentLine()
        {
            textEditor.SelectionStart = PromptOffset;
            textEditor.SelectionLength = Doc.TextLength - PromptOffset;
        }

        private string GetCurrentLineText()
        {
            var lastLine = Doc.GetLineByNumber(Doc.LineCount);
            var lastLineText = Doc.GetText(lastLine.Offset, lastLine.Length);
            if (lastLineText.Length >= prompt.Length)
                return lastLineText.Substring(prompt.Length);
            else
                return lastLineText;
        }
        #endregion

        #region TextEditor Write Text Helpers
        public void ClearLine()
        {
            var lastLine = Doc.GetLineByNumber(Doc.LineCount);
            Doc.Remove(lastLine.Offset, lastLine.Length);
            Doc.Insert(Doc.TextLength, prompt);
            MoveCaretToEnd();
        }

        public void WritePrompt()
        {
            //see if the last character is a new line, if not inser a new line
            if (!textEditor.Text.EndsWith(Environment.NewLine))
                WriteLine();
            Write(prompt, TextType.None);
        }

        public void Write(string text)
        {
            Write(text, TextType.Output);
        }

        public void Write(string text, TextType textType)
        {
            var startOffset = Doc.TextLength;
            Doc.Insert(Doc.TextLength, text);
            MoveCaretToEnd();
            var endOffset = Doc.TextLength;

            if (textType != TextType.None)
            {
                var colorizer = new OffsetColorizer(GetColor(textType)) { StartOffset = startOffset, EndOffset = endOffset };
                textEditor.TextArea.TextView.LineTransformers.Add(colorizer);
            }
        }

        public void WriteLine()
        {
            Write(Environment.NewLine, TextType.None);
        }

        public void WriteLine(string text)
        {
            Write(text + Environment.NewLine);
        }

        public void WriteLine(string text, TextType textType)
        {
            Write(text + Environment.NewLine, textType);
        }

        public Color GetColor(TextType textType)
        {
            switch (textType)
            {
                case TextType.Output:
                    return OutputColor;
                case TextType.Warning:
                    return WarningColor;
                case TextType.Error:
                    return ErrorColor;
                case TextType.Repl:
                    return ReplColor;
                case TextType.None:
                default:
                    return OutputColor;
            }
        }
        #endregion

      
    }//end class
}
