using System;
using System.Collections.Generic;

namespace ConjureBrowser.Core.Models;

public class SessionState
{
    public DateTimeOffset SavedAtUtc { get; set; }
    public int SelectedWebTabIndex { get; set; }
    public List<SessionTab> Tabs { get; set; } = new();
}
