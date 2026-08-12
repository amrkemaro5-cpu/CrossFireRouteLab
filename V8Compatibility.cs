using System.Drawing;

namespace CrossFireRouteLab;

// Compatibility shim for the v8 header's vector renderer. It keeps the
// generated dashboard source independent from the older theme fields.
readonly struct TextColor
{
    public static implicit operator Color(TextColor _) => Color.FromArgb(239, 246, 255);
}
