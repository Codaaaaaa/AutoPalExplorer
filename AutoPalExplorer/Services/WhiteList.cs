using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace AutoPalExplorer.Services;

public static class WhiteListCheck
{
    public static readonly HashSet<string> AllowList =
    [
        "19014409517518664", // qymy
        "19014409517278616", // wyz
        "19014409515783759", // hly
        "19014409516971300", // fuyu
        "19014409517529691", // didi
    ];

    public static bool IsPlayerAllowed(IClientState clientState)
    {
        if (clientState == null)
            return false;

        var cid = clientState.LocalContentId;

        if (cid == 0)
            return false;

        return AllowList.Contains(cid.ToString());
    }
}
