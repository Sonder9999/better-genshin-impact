namespace QuestVoice.Core;

public readonly record struct NormRect(double Left, double Top, double Right, double Bottom)
{
    public static NormRect From(double left, double top, double right, double bottom) => new(left, top, right, bottom);
    public NormRect Clamped() => new(
        Math.Clamp(Left, 0, 1), Math.Clamp(Top, 0, 1),
        Math.Clamp(Right, 0, 1), Math.Clamp(Bottom, 0, 1));
}
