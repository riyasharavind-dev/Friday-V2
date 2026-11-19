using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Friday.Desktop.Captions;

public partial class CaptionWindow : Window
{
    private sealed record CaptionWord(
        string Text,
        int Start,
        int Length);

    private readonly List<CaptionWord> _words = new();
    private readonly List<(int Start, int End)> _lines = new();

    private int _currentLine = -1;
    private int _currentWord = -1;

    public CaptionWindow()
    {
        InitializeComponent();
    }

    public void ShowCaption(string text)
    {
        _words.Clear();
        _lines.Clear();

        _currentLine = -1;
        _currentWord = -1;

        foreach (Match match in Regex.Matches(text, @"\S+"))
        {
            _words.Add(new CaptionWord(
                match.Value,
                match.Index,
                match.Length));
        }

        if (_words.Count == 0)
        {
            Hide();
            return;
        }

        Rect area = SystemParameters.WorkArea;

        Width = Math.Min(900, area.Width - 32);

        BuildLines();

        if (!IsVisible)
        {
            Show();
        }

        PositionCaption();
        RenderLine(0, -1);
    }

    private void BuildLines()
    {
        double availableWidth = Width - 64;

        int start = 0;
        string line = "";

        for (int i = 0; i < _words.Count; i++)
        {
            string candidate = string.IsNullOrEmpty(line)
                ? _words[i].Text
                : line + " " + _words[i].Text;

            if (i > start && MeasureText(candidate) > availableWidth)
            {
                _lines.Add((start, i));

                start = i;
                line = _words[i].Text;
            }
            else
            {
                line = candidate;
            }
        }

        _lines.Add((start, _words.Count));
    }

    private double MeasureText(string text)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(
                CaptionText.FontFamily,
                CaptionText.FontStyle,
                CaptionText.FontWeight,
                CaptionText.FontStretch),
            CaptionText.FontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        return formatted.WidthIncludingTrailingWhitespace;
    }

    public void HighlightSpeech(int characterPosition, int characterCount)
    {
        if (!IsVisible || _words.Count == 0)
        {
            return;
        }

        int wordIndex = -1;

        for (int i = 0; i < _words.Count; i++)
        {
            CaptionWord word = _words[i];

            if (characterPosition >= word.Start &&
                characterPosition < word.Start + word.Length)
            {
                wordIndex = i;
                break;
            }

            // Handle speech events positioned on whitespace.
            if (word.Start >= characterPosition)
            {
                wordIndex = i;
                break;
            }
        }

        if (wordIndex < 0 || wordIndex == _currentWord)
        {
            return;
        }

        for (int i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];

            if (wordIndex >= line.Start && wordIndex < line.End)
            {
                RenderLine(i, wordIndex);
                return;
            }
        }
    }

    private void RenderLine(int lineIndex, int activeWord)
    {
        _currentLine = lineIndex;
        _currentWord = activeWord;

        CaptionText.Inlines.Clear();

        var line = _lines[_currentLine];

        string fullLine = "";

        for (int i = line.Start; i < line.End; i++)
        {
            if (i > line.Start)
            {
                CaptionText.Inlines.Add(new Run(" "));
                fullLine += " ";
            }

            CaptionText.Inlines.Add(new Run(_words[i].Text)
            {
                Foreground = i == activeWord
                    ? Brushes.Gold
                    : Brushes.White
            });

            fullLine += _words[i].Text;
        }

        // Fit an unusually long word without wrapping to a second line.
        double measuredWidth = MeasureText(fullLine);
        double availableWidth = Width - 64;

        CaptionText.LayoutTransform =
            measuredWidth > availableWidth
                ? new ScaleTransform(
                    availableWidth / measuredWidth,
                    availableWidth / measuredWidth)
                : Transform.Identity;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PositionCaption();
    }

    private void PositionCaption()
    {
        Rect area = SystemParameters.WorkArea;

        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight + 8;
    }
}