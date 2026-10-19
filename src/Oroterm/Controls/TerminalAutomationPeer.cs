using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Text;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using System.Globalization;

namespace Oroterm.Controls;

internal sealed class TerminalInputBox : TextBox
{
    public TerminalView? Terminal { get; set; }
    protected override AutomationPeer OnCreateAutomationPeer() => new InputPeer(this);
    private sealed class InputPeer(TerminalInputBox owner) : TextBoxAutomationPeer(owner)
    {
        protected override object GetPatternCore(PatternInterface pattern) => pattern is PatternInterface.Text or PatternInterface.Text2 && owner.Terminal is { } terminal
            ? terminal.TextPeer : base.GetPatternCore(pattern);
    }
}

internal sealed record AccessibleTerminalText(string Text, int[] CellOffsets, int Columns, int Rows, int Cursor, int SelectionStart, int SelectionEnd);

/// <summary>Read-only viewport text, caret, selection and screen geometry for UI Automation clients.</summary>
internal sealed class TerminalAutomationPeer(TerminalView owner) : FrameworkElementAutomationPeer(owner), ITextProvider, ITextProvider2
{
    protected override string GetClassNameCore() => "Oroterm.Terminal";
    protected override string GetNameCore() => "Terminal output";
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
    protected override bool IsKeyboardFocusableCore() => true;
    protected override bool HasKeyboardFocusCore() => owner.InputHasFocus;
    protected override object GetPatternCore(PatternInterface pattern) => pattern is PatternInterface.Text or PatternInterface.Text2 ? this : base.GetPatternCore(pattern);
    protected override void SetFocusCore() => owner.FocusTerminal();
    internal IRawElementProviderSimple Provider => ProviderFromPeer(this);
    internal TerminalView Terminal => owner;
    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;
    public ITextRangeProvider DocumentRange { get { var text = owner.AccessibleText(); return new Range(this, text, 0, text.Text.Length); } }
    public ITextRangeProvider[] GetSelection()
    {
        var text = owner.AccessibleText();
        return [new Range(this, text, text.SelectionStart < 0 ? text.Cursor : text.SelectionStart, text.SelectionEnd < 0 ? text.Cursor : text.SelectionEnd)];
    }
    public ITextRangeProvider[] GetVisibleRanges() => [DocumentRange];
    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) => throw new ArgumentException("The terminal has no embedded text children.");
    public ITextRangeProvider RangeFromPoint(Point screenLocation)
    {
        var text = owner.AccessibleText(); int cell = owner.AccessibleCellAt(screenLocation);
        int offset = text.CellOffsets[Math.Clamp(cell, 0, text.CellOffsets.Length - 1)];
        return new Range(this, text, offset, offset);
    }
    public ITextRangeProvider GetCaretRange(out bool isActive)
    {
        isActive = owner.InputHasFocus; var text = owner.AccessibleText(); return new Range(this, text, text.Cursor, text.Cursor);
    }
    public ITextRangeProvider RangeFromAnnotation(IRawElementProviderSimple annotationElement) => throw new ArgumentException("The terminal has no annotations.");
    private sealed class Range(TerminalAutomationPeer peer, AccessibleTerminalText document, int start, int end) : ITextRangeProvider
    {
        private int _start = start, _end = end;
        private int Endpoint(TextPatternRangeEndpoint endpoint) => endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        private void SetEndpoint(TextPatternRangeEndpoint endpoint, int value)
        {
            value = Math.Clamp(value, 0, document.Text.Length);
            if (endpoint == TextPatternRangeEndpoint.Start) { _start = value; if (_end < value) _end = value; }
            else { _end = value; if (_start > value) _start = value; }
        }
        private TerminalAutomationPeer Peer => peer;
        private Range Other(ITextRangeProvider value) => value is Range range && ReferenceEquals(range.Peer, peer) ? range : throw new ArgumentException("Range belongs to another document.");
        public ITextRangeProvider Clone() => new Range(peer, document, _start, _end);
        public bool Compare(ITextRangeProvider textRangeProvider) => textRangeProvider is Range range && ReferenceEquals(peer, range.Peer)
            && range._start == _start && range._end == _end && (ReferenceEquals(document, range.Document) || document.Text == range.Document.Text);
        private AccessibleTerminalText Document => document;
        public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider textRangeProvider, TextPatternRangeEndpoint targetEndpoint) => Endpoint(endpoint).CompareTo(Other(textRangeProvider).Endpoint(targetEndpoint));
        public string GetText(int maxLength) => document.Text.Substring(_start, maxLength < 0 ? _end - _start : Math.Min(maxLength, _end - _start));
        public IRawElementProviderSimple GetEnclosingElement() => peer.Provider;
        public IRawElementProviderSimple[] GetChildren() => [];
        public object GetAttributeValue(int attributeId) => attributeId == (int)Microsoft.UI.Xaml.AutomationTextAttributesEnum.IsReadOnlyAttribute ? true : null!;
        public ITextRangeProvider FindAttribute(int attributeId, object value, bool backward) => attributeId == (int)Microsoft.UI.Xaml.AutomationTextAttributesEnum.IsReadOnlyAttribute && value is true ? Clone() : null!;
        public ITextRangeProvider FindText(string text, bool backward, bool ignoreCase)
        {
            if (string.IsNullOrEmpty(text)) throw new ArgumentException("Search text must be nonempty.");
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var span = GetText(-1); int offset = backward ? span.LastIndexOf(text, comparison) : span.IndexOf(text, comparison);
            return offset < 0 ? null! : new Range(peer, document, _start + offset, _start + offset + text.Length);
        }
        private int[] Boundaries(TextUnit unit)
        {
            var boundaries = new SortedSet<int> { 0, document.Text.Length };
            if (unit == TextUnit.Character) foreach (int offset in StringInfo.ParseCombiningCharacters(document.Text)) boundaries.Add(offset);
            else if (unit == TextUnit.Word)
            {
                for (int i = 1; i < document.Text.Length; i++)
                    if (char.IsWhiteSpace(document.Text[i - 1]) != char.IsWhiteSpace(document.Text[i])) boundaries.Add(i);
            }
            else if (unit is TextUnit.Line or TextUnit.Paragraph or TextUnit.Format)
                for (int i = 0; i < document.Text.Length; i++) if (document.Text[i] == '\n') boundaries.Add(i + 1);
            return boundaries.ToArray();
        }
        public void ExpandToEnclosingUnit(TextUnit unit)
        {
            var boundaries = Boundaries(unit);
            int index = Math.Clamp(Array.BinarySearch(boundaries, _start) is var found && found >= 0 ? found : ~found - 1, 0, boundaries.Length - 1);
            if (index == boundaries.Length - 1 && index > 0) index--;
            _start = boundaries[index]; _end = boundaries[Math.Min(index + 1, boundaries.Length - 1)];
        }
        public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
        {
            if (count == 0) return 0;
            var boundaries = Boundaries(unit); int value = Endpoint(endpoint), moved = 0;
            int steps = (int)Math.Min(Math.Abs((long)count), boundaries.Length);
            for (int i = 0; i < steps; i++)
            {
                int index = Array.BinarySearch(boundaries, value);
                index = count > 0 ? index >= 0 ? index + 1 : ~index : index >= 0 ? index - 1 : ~index - 1;
                if (index < 0 || index >= boundaries.Length) break;
                value = boundaries[index]; moved += Math.Sign(count);
            }
            SetEndpoint(endpoint, value); return moved;
        }
        public int Move(TextUnit unit, int count)
        {
            if (count == 0) return 0;
            bool degenerate = _start == _end;
            if (!degenerate)
            {
                ExpandToEnclosingUnit(unit);
                var boundaries = Boundaries(unit);
                int index = Math.Max(0, Array.BinarySearch(boundaries, _start));
                int target = (int)Math.Clamp((long)index + count, 0, Math.Max(0, boundaries.Length - 2));
                _start = boundaries[target]; _end = boundaries[Math.Min(target + 1, boundaries.Length - 1)];
                return target - index;
            }
            int moved = MoveEndpointByUnit(TextPatternRangeEndpoint.Start, unit, count);
            _end = _start;
            return moved;
        }
        public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider textRangeProvider, TextPatternRangeEndpoint targetEndpoint) => SetEndpoint(endpoint, Other(textRangeProvider).Endpoint(targetEndpoint));
        public void GetBoundingRectangles(out double[] returnValue) => returnValue = peer.Terminal.AccessibleRectangles(document, _start, _end);
        public void Select() => peer.Terminal.AccessibleSelect(document, _start, _end);
        public void AddToSelection() => throw new InvalidOperationException("Terminal supports one selection.");
        public void RemoveFromSelection() => peer.Terminal.AccessibleSelect(document, 0, 0);
        public void ScrollIntoView(bool alignToTop) { /* Document is the visible viewport. */ }
    }
}
