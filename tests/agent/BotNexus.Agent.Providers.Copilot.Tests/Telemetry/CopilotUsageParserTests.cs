using System.Text.Json;
using BotNexus.Agent.Providers.Copilot.Telemetry;

namespace BotNexus.Agent.Providers.Copilot.Tests.Telemetry;

/// <summary>
/// Pins <see cref="CopilotUsageParser"/> against the exact wire shape captured
/// from real Copilot CLI traffic (chat-completions body, Anthropic-Messages
/// <c>message_delta</c> SSE event, Responses <c>response.completed</c> SSE event).
/// </summary>
public class CopilotUsageParserTests
{
    private const string SampleJson = """
        {
          "copilot_usage": {
            "token_details": [
              { "batch_size": 1000000, "cost_per_batch": 30000000000,  "token_count": 157, "token_type": "input"       },
              { "batch_size": 1000000, "cost_per_batch": 15000000000,  "token_count":   0, "token_type": "cache_read"  },
              { "batch_size": 1000000, "cost_per_batch":           0,  "token_count":   0, "token_type": "cache_write" },
              { "batch_size": 1000000, "cost_per_batch": 120000000000, "token_count":  14, "token_type": "output"      }
            ],
            "total_nano_aiu": 6390000
          }
        }
        """;

    [Fact]
    public void TryParse_PopulatesEveryField()
    {
        using var doc = JsonDocument.Parse(SampleJson);

        var ok = CopilotUsageParser.TryParse(doc.RootElement, out var usage);

        ok.ShouldBeTrue();
        usage.TotalNanoAiu.ShouldBe(6_390_000);
        usage.TokenDetails.Count.ShouldBe(4);

        var input = usage.TokenDetails.Single(d => d.TokenType == "input");
        input.TokenCount.ShouldBe(157);
        input.BatchSize.ShouldBe(1_000_000);
        input.CostPerBatch.ShouldBe(30_000_000_000);

        var output = usage.TokenDetails.Single(d => d.TokenType == "output");
        output.TokenCount.ShouldBe(14);
        output.CostPerBatch.ShouldBe(120_000_000_000);

        usage.TokenDetails.ShouldContain(d => d.TokenType == "cache_read");
        usage.TokenDetails.ShouldContain(d => d.TokenType == "cache_write");
    }

    [Fact]
    public void TryParse_NoCopilotUsageProperty_ReturnsFalse()
    {
        using var doc = JsonDocument.Parse("""{ "usage": { "input_tokens": 5 } }""");

        CopilotUsageParser.TryParse(doc.RootElement, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryParse_MalformedCopilotUsage_ReturnsFalse()
    {
        using var doc = JsonDocument.Parse("""{ "copilot_usage": "not-an-object" }""");

        CopilotUsageParser.TryParse(doc.RootElement, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryParse_EmptyTokenDetails_YieldsEmptyList()
    {
        using var doc = JsonDocument.Parse("""
            { "copilot_usage": { "token_details": [], "total_nano_aiu": 0 } }
            """);

        CopilotUsageParser.TryParse(doc.RootElement, out var usage).ShouldBeTrue();
        usage.TotalNanoAiu.ShouldBe(0);
        usage.TokenDetails.ShouldBeEmpty();
    }

    [Fact]
    public void TryParse_UnknownTokenType_IsPreservedVerbatim()
    {
        using var doc = JsonDocument.Parse("""
            {
              "copilot_usage": {
                "token_details": [
                  { "batch_size": 1000000, "cost_per_batch": 1, "token_count": 1, "token_type": "future_field" }
                ],
                "total_nano_aiu": 1
              }
            }
            """);

        CopilotUsageParser.TryParse(doc.RootElement, out var usage).ShouldBeTrue();
        usage.TokenDetails.Single().TokenType.ShouldBe("future_field");
    }

    [Fact]
    public void TryParse_MessageDeltaShape_WorksOnRealCapturedFixture()
    {
        // Verbatim shape from cat_v1_messages.txt (numbers preserved as captured).
        using var doc = JsonDocument.Parse("""
            {
              "copilot_usage": {
                "token_details": [
                  { "batch_size": 1000000, "cost_per_batch":  500000000000, "token_count":     6, "token_type": "input"       },
                  { "batch_size": 1000000, "cost_per_batch":   50000000000, "token_count":     0, "token_type": "cache_read"  },
                  { "batch_size": 1000000, "cost_per_batch":  625000000000, "token_count": 35112, "token_type": "cache_write" },
                  { "batch_size": 1000000, "cost_per_batch": 2500000000000, "token_count":    80, "token_type": "output"      }
                ],
                "total_nano_aiu": 22148000000
              },
              "delta": { "stop_reason": "tool_use" },
              "type": "message_delta",
              "usage": { "input_tokens": 6, "output_tokens": 80 }
            }
            """);

        CopilotUsageParser.TryParse(doc.RootElement, out var usage).ShouldBeTrue();
        usage.TotalNanoAiu.ShouldBe(22_148_000_000);
        usage.TokenDetails.Single(d => d.TokenType == "cache_write").TokenCount.ShouldBe(35112);
    }
}
