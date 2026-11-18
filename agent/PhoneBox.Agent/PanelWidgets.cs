namespace PhoneBox.Agent;

/// <summary>Finding the one widget a phone request comes from, for features that act on the computer per widget.</summary>
public static class PanelWidgets
{
    /// <summary>
    /// The widget of <paramref name="type"/> the phone names as "pageId/widgetId" ("pageId/stackId:cardId" for a Smart
    /// Stack card), if it is on an enabled page this phone shows and (in a stack) an enabled card. Null otherwise: there
    /// is no fallback to another widget, so each widget's own options decide.
    /// </summary>
    public static WidgetConfig? Find(AgentConfig c, string? deviceId, string? key, string type) => string.IsNullOrEmpty(key) ? null
        : c.PagesFor(deviceId).Where(p => p.Enabled).SelectMany(p => (p.Widgets ?? []).SelectMany(w =>
                new[] { (Key: p.Id + "/" + w.Id, W: w) }.Concat(StackWidgets.Children(w).Where(ch => ch.Enabled)
                    .Select(ch => (Key: p.Id + "/" + w.Id + ":" + ch.Id, W: new WidgetConfig { Id = ch.Id, Type = ch.Type, Options = ch.Options })))))
            .FirstOrDefault(x => x.W.Type == type && x.Key == key).W;
}
