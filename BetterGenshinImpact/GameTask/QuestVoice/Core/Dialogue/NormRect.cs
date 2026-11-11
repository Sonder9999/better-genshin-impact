using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace BetterGenshinImpact.GameTask.QuestVoice.Domain;

public readonly record struct NormRect(double Left, double Top, double Right, double Bottom)
{
    public static NormRect From(double left, double top, double right, double bottom) => new(left, top, right, bottom);
    public NormRect Clamped() => new(
        Math.Clamp(Left, 0, 1), Math.Clamp(Top, 0, 1),
        Math.Clamp(Right, 0, 1), Math.Clamp(Bottom, 0, 1));
}
