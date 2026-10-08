using LaunchPad.Models;
using LaunchPad.Services;
using LaunchPad.Views;
using Xunit;

namespace LaunchPad.Tests;

public sealed class ProjectIdentityTests
{
    [Fact]
    public void ApprovedPaletteAndSharedRingTabAssignmentSurviveDisplaySorting()
    {
        var expected = new[] { "#3A4558", "#4E5E42", "#6E4560", "#457079", "#4F5A8A", "#6E627A", "#4A5F7A", "#456055",
            "#5C6570", "#7A5E6A", "#5E4A62", "#6A4552", "#2F5558", "#5A486E", "#5A6B58", "#635A82" };
        var paths = Enumerable.Range(0, 16).Select(i => Path.Combine(Path.GetTempPath(), "identity-" + i)).ToArray();
        var display = paths.Reverse().Select(p => new ProjectEntry { Path = p, Name = "Renamed" }).ToArray();
        for (var i = 0; i < 16; i++)
        {
            var index = ProjectIdentity.IndexFor(paths[i] + Path.DirectorySeparatorChar, paths, display);
            Assert.Equal(i, index);
            var color = IdentityPalette.At(index);
            Assert.Equal(expected[i], $"#{color.R:X2}{color.G:X2}{color.B:X2}");
        }
    }
}
