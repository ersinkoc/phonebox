using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class LockPhoneOnShortcutTests
{
    [Fact]
    public void DisplayConfig_LockPhoneWithShortcut_DefaultsToFalse()
    {
        var config = new AgentConfig();
        Assert.False(config.Display.LockPhoneWithShortcut);
    }

    [Fact]
    public void BuiltinLock_Invokes_OnLocked_Callback()
    {
        var called = false;
        AgentConfig? passedConfig = null;

        var prevHandler = Shortcuts.OnLocked;
        try
        {
            Shortcuts.OnLocked = cfg =>
            {
                called = true;
                passedConfig = cfg;
            };

            var cfg = new AgentConfig();
            cfg.Display.LockPhoneWithShortcut = true;
            cfg.Shortcuts = [new Shortcut { Id = "test-lock", Label = "Lock", Cmd = "builtin:lock" }];

            Shortcuts.Run(cfg, "test-lock", NullLogger.Instance);

            Assert.True(called);
            Assert.Same(cfg, passedConfig);
        }
        finally
        {
            Shortcuts.OnLocked = prevHandler;
        }
    }
}
