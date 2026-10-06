using TalkWatch.Core.Calls;

namespace TalkWatch.Web.Components;

/// <summary>What each kind of line is called on screen, one or several: never the name it has in the code.</summary>
public static class LineKindNames
{
    public static string One(LineKind kind) => kind switch
    {
        LineKind.Did => "Number",
        LineKind.User => "Person",
        LineKind.RingGroup => "Ring group",
        LineKind.Attendant => "Switchboard",
        LineKind.Queue => "Queue",
        _ => "Outside number",
    };

    public static string Many(LineKind kind) => kind switch
    {
        LineKind.Did => "Numbers",
        LineKind.User => "People (extensions)",
        LineKind.RingGroup => "Ring groups",
        LineKind.Attendant => "Switchboards",
        LineKind.Queue => "Queues",
        _ => "Contacts",
    };
}
