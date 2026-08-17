using System.Text;

namespace AgentUi;

/// <summary>Разбирает поток токенов: отделяет <think>...</think> от основного текста.</summary>
public class ThinkTagSplitter
{
    private const string Open = "<think>";
    private const string Close = "</think>";
    private bool _inThink;
    private string _pending = "";

    public (string content, string thinking) Process(string token)
    {
        var sbC = new StringBuilder();
        var sbT = new StringBuilder();
        var text = _pending + token;
        _pending = "";
        int i = 0;

        while (i < text.Length)
        {
            if (_inThink)
            {
                int close = text.IndexOf(Close, i, StringComparison.Ordinal);
                if (close >= 0)
                {
                    sbT.Append(text, i, close - i);
                    _inThink = false;
                    i = close + Close.Length;
                }
                else
                {
                    int safe = text.Length;
                    for (int k = 1; k < Close.Length; k++)
                        if (text.EndsWith(Close[..k], StringComparison.Ordinal)) { safe = text.Length - k; break; }
                    sbT.Append(text, i, safe - i);
                    _pending = text[safe..];
                    i = text.Length;
                }
            }
            else
            {
                int open = text.IndexOf(Open, i, StringComparison.Ordinal);
                if (open >= 0)
                {
                    sbC.Append(text, i, open - i);
                    _inThink = true;
                    i = open + Open.Length;
                }
                else
                {
                    int safe = text.Length;
                    for (int k = 1; k < Open.Length; k++)
                        if (text.EndsWith(Open[..k], StringComparison.Ordinal)) { safe = text.Length - k; break; }
                    sbC.Append(text, i, safe - i);
                    _pending = text[safe..];
                    i = text.Length;
                }
            }
        }

        return (sbC.ToString(), sbT.ToString());
    }

    public (string content, string thinking) Flush()
    {
        var p = _pending;
        _pending = "";
        return _inThink ? ("", p) : (p, "");
    }
}