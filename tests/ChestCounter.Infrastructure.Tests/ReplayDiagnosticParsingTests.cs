using System.Text.Json;
using ChestCounter;
using FortniteReplayReader.Models;
using Unreal.Core.Models.Enums;
using Xunit;

namespace ChestCounter.Infrastructure.Tests;

public sealed class ReplayDiagnosticParsingTests
{
    [Fact]
    public void SerializationPreservesNonFiniteValuesNullsAndEmptyCollections()
    {
        var replay = new FortniteReplay();
        replay.GameData.MatchEndTime = float.NaN;
        replay.GameData.AircraftStartTime = float.PositiveInfinity;
        replay.GameData.SafeZonesStartTime = float.NegativeInfinity;

        var json = ReplayDiagnosticParsingTranscoder.SerializeReplay(replay);

        using var document = JsonDocument.Parse(json);
        var gameData = document.RootElement.GetProperty("GameData");
        Assert.Equal("NaN", gameData.GetProperty("MatchEndTime").GetString());
        Assert.Equal("Infinity", gameData.GetProperty("AircraftStartTime").GetString());
        Assert.Equal("-Infinity", gameData.GetProperty("SafeZonesStartTime").GetString());
        Assert.Equal(JsonValueKind.Null, gameData.GetProperty("MapInfo").ValueKind);
        Assert.Empty(gameData.GetProperty("ActiveGameplayModifiers").EnumerateArray());
        Assert.Contains(Environment.NewLine, json);
    }

    [Fact]
    public async Task MissingFilePreservesOriginalExceptionAndStage()
    {
        var file = new FileInfo(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.replay"));

        var error = await Assert.ThrowsAsync<ReplayDiagnosticParsingException>(
            () => ReplayDiagnosticParsingTranscoder.TranscodeAsync(file, ParseMode.EventsOnly));

        Assert.Equal(ReplayDiagnosticParsingStage.FileAccess, error.Stage);
        Assert.IsType<FileNotFoundException>(error.InnerException);
        Assert.Contains("EventsOnly", error.Message);
        Assert.Contains(nameof(FileNotFoundException), error.ToString());
    }

    [Theory]
    [InlineData(ParseMode.EventsOnly)]
    [InlineData(ParseMode.Minimal)]
    [InlineData(ParseMode.Normal)]
    [InlineData(ParseMode.Full)]
    public async Task InvalidReplayReportsReadStageAndOriginalException(ParseMode mode)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.replay");
        try
        {
            await File.WriteAllBytesAsync(path, new byte[8]);

            var error = await Assert.ThrowsAsync<ReplayDiagnosticParsingException>(
                () => ReplayDiagnosticParsingTranscoder.TranscodeAsync(new FileInfo(path), mode));

            Assert.Equal(ReplayDiagnosticParsingStage.ReadDecodeParseAndBuild, error.Stage);
            Assert.NotNull(error.InnerException);
            Assert.Contains(mode.ToString(), error.Message);
            Assert.Contains(error.InnerException.GetType().FullName!, error.ToString());
            Assert.NotNull(error.InnerException.StackTrace);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UnsupportedModeReportsInitializationStage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.replay");
        try
        {
            await File.WriteAllBytesAsync(path, Array.Empty<byte>());

            var error = await Assert.ThrowsAsync<ReplayDiagnosticParsingException>(
                () => ReplayDiagnosticParsingTranscoder.TranscodeAsync(new FileInfo(path), ParseMode.Debug));

            Assert.Equal(ReplayDiagnosticParsingStage.ParserInitialization, error.Stage);
            Assert.IsType<ArgumentOutOfRangeException>(error.InnerException);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
