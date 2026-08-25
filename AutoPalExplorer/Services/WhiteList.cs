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
        "18014469509979413", // heimofashi
        "19014409514142779", // laladi
        "18014469510044058", // baimofashi
        "18014449513731067", // huashengkakaer
        "18014469510937372", // xiazhong
        "19014409515331231", // kakaliya
    ];

    public static bool IsPlayerAllowed(IPlayerState playerState)
    {
        if (playerState == null || !playerState.IsLoaded)
            return false;

        var cid = playerState.ContentId;

        if (cid == 0)
            return false;

        return AllowList.Contains(cid.ToString());
    }
}
