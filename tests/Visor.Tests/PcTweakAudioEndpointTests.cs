using Visor.Services;
using Xunit;

namespace Visor.Tests;

/// <summary>Pure parts of the audio default-device fix (no audio stack touched).</summary>
public class PcTweakAudioEndpointTests
{
    [Fact]
    public void TryParse_reads_render_and_capture_endpoint_ids()
    {
        Assert.True(AudioEndpointId.TryParse("{0.0.0.00000000}.{3c662cc2-4bcf-43ee-9fc0-127246e3bb71}", out var flow, out var g));
        Assert.Equal(AudioEndpointId.Flow.Render, flow);
        Assert.Equal(Guid.Parse("3c662cc2-4bcf-43ee-9fc0-127246e3bb71"), g);

        Assert.True(AudioEndpointId.TryParse(" {0.0.1.00000000}.{13332C2C-B838-45DA-91D4-3AEBA8F636D5} ", out flow, out _));
        Assert.Equal(AudioEndpointId.Flow.Capture, flow);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Speakers (Realtek)")]
    [InlineData("{0.0.0.00000000}")]
    [InlineData("{0.0.0.00000000}.{not-a-guid}")]
    [InlineData("{0.0.0}.{3c662cc2-4bcf-43ee-9fc0-127246e3bb71}")]
    [InlineData("{0.0.0.zz}.{3c662cc2-4bcf-43ee-9fc0-127246e3bb71}")]
    public void TryParse_rejects_names_and_malformed_ids(string? id) =>
        Assert.False(AudioEndpointId.TryParse(id, out _, out _));

    [Theory]
    [InlineData("{0.0.0.00000000}.{3c662cc2-4bcf-43ee-9fc0-127246e3bb71}", true)]
    [InlineData("  {x", true)]
    [InlineData("Headphones", false)]
    [InlineData("", false)]
    public void LooksLikeId(string text, bool expected) => Assert.Equal(expected, AudioEndpointId.LooksLikeId(text));

    [Fact]
    public void DescribeHResult_names_the_codes_seen_in_the_field()
    {
        Assert.StartsWith("0x80070490 E_NOTFOUND", AudioEndpointId.DescribeHResult(AudioEndpointId.E_NOTFOUND));
        Assert.StartsWith("0x80004002 E_NOINTERFACE", AudioEndpointId.DescribeHResult(AudioEndpointId.E_NOINTERFACE));
        Assert.StartsWith("0x88890004 AUDCLNT_E_DEVICE_INVALIDATED", AudioEndpointId.DescribeHResult(AudioEndpointId.AUDCLNT_E_DEVICE_INVALIDATED));
        Assert.Equal("0x80004005", AudioEndpointId.DescribeHResult(unchecked((int)0x80004005)));
        Assert.Equal("unplugged", AudioEndpointId.DescribeState(8));
        Assert.Equal("state 0x10000001", AudioEndpointId.DescribeState(0x10000001));
    }

    [Fact]
    public void Memo_backs_off_after_a_failure_and_retries_after_the_window()
    {
        var memo = new AudioSwitchMemo();
        var t0 = new DateTime(2026, 10, 13, 18, 53, 52, DateTimeKind.Utc);
        const string id = "{0.0.0.00000000}.{13332c2c-b838-45da-91d4-3aeba8f636d5}";

        Assert.True(memo.ShouldAttempt(id, t0));
        memo.RecordFailure(id, AudioEndpointId.E_NOTFOUND, t0);
        Assert.False(memo.ShouldAttempt(id, t0.AddSeconds(3)));   // the deferred re-apply 3 s later
        Assert.False(memo.ShouldAttempt(id.ToUpperInvariant(), t0.AddMinutes(9)));
        Assert.Equal(AudioEndpointId.E_NOTFOUND, memo.LastFailure(id));
        Assert.True(memo.ShouldAttempt(id, t0 + AudioSwitchMemo.RetryAfter));
        Assert.Null(memo.LastFailure(id));

        memo.RecordFailure(id, AudioEndpointId.E_NOTFOUND, t0);
        memo.RecordSuccess(id);
        Assert.True(memo.ShouldAttempt(id, t0));
    }

    [Fact]
    public void Memo_reports_an_unavailable_device_once_until_it_works_again()
    {
        var memo = new AudioSwitchMemo();
        Assert.True(memo.FirstUnavailableReport("dev"));
        Assert.False(memo.FirstUnavailableReport("dev"));
        Assert.False(memo.FirstUnavailableReport("DEV"));
        memo.RecordSuccess("dev");
        Assert.True(memo.FirstUnavailableReport("dev"));
    }
}
