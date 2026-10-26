using System.Collections.Generic;
using System.Linq;
using InWorldz.Phlox.Types;
using Xunit;

namespace InWorldz.Phlox.Tests;

public class YEngineFunctionParityTests
{
    [Fact]
    public void PreviouslyMissingYEngineFunctionsAreRegisteredAndHaveUniqueDispatches()
    {
        string[] expected =
        {
            "osGiveLinkInventory", "osGiveLinkInventoryList", "osMessageAttachments",
            "osNpcLookAt", "osNpcSayTo", "osReplaceAgentEnvironment",
            "osReplaceParcelEnvironment", "osReplaceRegionEnvironment", "osResetEnvironment",
            "osWindVolumeCreate", "osWindVolumeRemove", "osWindVolumes", "llRemoteLoadScript",
            "modInvokeN", "modInvokeS", "modInvokeI", "modInvokeF", "modInvokeK", "modInvokeV", "modInvokeR",
            "modInvokeL", "modSendCommand",
            "lsGetWindlightScene", "lsSetWindlightScene", "lsSetWindlightSceneTargeted", "lsClearWindlightScene",
            "osMakeNotecard"
        };

        foreach (string name in expected)
            Assert.True(Defaults.SystemMethods.ContainsKey(name), $"{name} is missing from the Phlox compiler function table");

        var all = Defaults.SystemMethods.Values.SelectMany(group => group).ToList();
        Assert.Equal(all.Count, all.Select(sig => sig.TableIndex).Distinct().Count());
        foreach (string name in expected.Where(name => name.StartsWith("modInvoke", System.StringComparison.Ordinal)))
            Assert.True(Defaults.SystemMethods[name][0].IsVariadic, $"{name} must accept module-defined argument lists");
    }

    [Fact]
    public void LightShareFunctionFormsCompile()
    {
        var compile = PhloxCompiler.CompileInDefault(@"
            list scene = lsGetWindlightScene([]);
            integer setResult = lsSetWindlightScene([]);
            integer targetedResult = lsSetWindlightSceneTargeted([], ""00000000-0000-0000-0000-000000000001"");
            lsClearWindlightScene();
        ");
        Assert.False(compile.HasErrors(), compile.Report);
    }
}
