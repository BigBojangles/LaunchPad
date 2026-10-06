using System.Reflection;

namespace LaunchPad.Services;

public static class AgentBobInstructions
{
    public static string Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("AgentBobInstructions.txt", StringComparison.OrdinalIgnoreCase));

        if (name is null)
            return "";

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
            return "";

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().TrimEnd() + Environment.NewLine;
    }
}
