using System;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using ActionSheet = Lumina.Excel.Sheets.Action;

namespace AutoPalExplorer.Debug;

public static class PomanderDebugger
{
    public static void DumpPomanderSheets(IDataManager data, IPluginLog log)
    {
        DumpItems(data, log);
        log.Information("ACtions next");
        DumpActions(data, log);
    }

    private static void DumpItems(IDataManager data, IPluginLog log)
    {
        var items = data.GetExcelSheet<Item>();
        if (items == null)
        {
            log.Error("[PomanderDebug] Item sheet missing.");
            return;
        }

        foreach (var row in items)
        {
            // row: Item (struct)
            var name = row.Name.ToString();
            if (!string.IsNullOrEmpty(name) && name.Contains("魔陶器", StringComparison.Ordinal))
            {
                log.Information($"[PomanderDebug][Item] RowId={row.RowId}, Name={name}, UseAction(ActionType.Item, {row.RowId})");
            }
        }
    }

    private static void DumpActions(IDataManager data, IPluginLog log)
    {
        var actions = data.GetExcelSheet<ActionSheet>();
        if (actions == null)
        {
            log.Error("[PomanderDebug] Action sheet missing.");
            return;
        }

        foreach (var row in actions)
        {
            var name = row.Name.ToString();
            if (string.IsNullOrEmpty(name))
                continue;

            if (name.Contains("魔陶器", StringComparison.Ordinal) ||
                name.Contains("Pomander", StringComparison.OrdinalIgnoreCase))
            {
                log.Information($"[PomanderDebug][Action] RowId={row.RowId}, Name={name}, UseAction(ActionType.Action, {row.RowId})");
            }
        }
    }
}
