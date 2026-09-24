using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PEIS.Report.Contracts;
using Xunit;

namespace PEIS.Report.Engine.Tests;

public sealed class WatermarkResolverTests
{
    private readonly StubWatermarkTextProvider _hospitalProvider = new("中心人民医院");

    [Fact]
    public async Task When_disabled_by_default_returns_disabled_watermark()
    {
        var options = Options.Create(new WatermarkPolicyOptions { Enabled = false });
        var resolver = new DefaultWatermarkResolver(options, _hospitalProvider);

        var definition = CreateDefinition("jktjbbd");
        var request = new ReportRenderRequest("jktjbbd", new Dictionary<string, JsonElement>());
        var data = CreateReportData(("xm", "张三"));

        var result = await resolver.ResolveAsync(definition, request, data, CancellationToken.None);

        Assert.False(result.Enabled);
    }

    [Fact]
    public async Task When_report_is_in_exclude_list_returns_disabled_watermark()
    {
        var options = Options.Create(new WatermarkPolicyOptions
        {
            Enabled = true,
            ExcludeReports = "xmtm,tjdj",
            Field = "xm"
        });
        var resolver = new DefaultWatermarkResolver(options, _hospitalProvider);

        var definition = CreateDefinition("xmtm");
        var request = new ReportRenderRequest("xmtm", new Dictionary<string, JsonElement>());
        var data = CreateReportData(("xm", "李四"));

        var result = await resolver.ResolveAsync(definition, request, data, CancellationToken.None);

        Assert.False(result.Enabled);
    }

    [Fact]
    public async Task When_multiple_candidate_fields_configured_resolves_different_columns_for_different_reports()
    {
        var options = Options.Create(new WatermarkPolicyOptions
        {
            Enabled = true,
            Field = "xm,hzxm,b_name,name" // 解决不同报表列名不一致问题
        });
        var resolver = new DefaultWatermarkResolver(options, _hospitalProvider);

        // 报告 A: 字段叫 xm
        var reportA = CreateDefinition("rep_a");
        var dataA = CreateReportData(("xm", "张三"), ("age", "30"));
        var resultA = await resolver.ResolveAsync(reportA, new ReportRenderRequest("rep_a", new Dictionary<string, JsonElement>()), dataA, CancellationToken.None);
        Assert.True(resultA.Enabled);
        Assert.Equal("张三", resultA.Text);

        // 报告 B: 字段叫 hzxm
        var reportB = CreateDefinition("rep_b");
        var dataB = CreateReportData(("hzxm", "李四"), ("dept", "车间"));
        var resultB = await resolver.ResolveAsync(reportB, new ReportRenderRequest("rep_b", new Dictionary<string, JsonElement>()), dataB, CancellationToken.None);
        Assert.True(resultB.Enabled);
        Assert.Equal("李四", resultB.Text);

        // 报告 C: 字段叫 b_name
        var reportC = CreateDefinition("rep_c");
        var dataC = CreateReportData(("b_name", "王五"));
        var resultC = await resolver.ResolveAsync(reportC, new ReportRenderRequest("rep_c", new Dictionary<string, JsonElement>()), dataC, CancellationToken.None);
        Assert.True(resultC.Enabled);
        Assert.Equal("王五", resultC.Text);

        // 报告 D: 候选字段全都没有，自动回退到医院机构名称
        var reportD = CreateDefinition("rep_d");
        var dataD = CreateReportData(("other_col", "123"));
        var resultD = await resolver.ResolveAsync(reportD, new ReportRenderRequest("rep_d", new Dictionary<string, JsonElement>()), dataD, CancellationToken.None);
        Assert.True(resultD.Enabled);
        Assert.Equal("中心人民医院", resultD.Text);
    }

    [Fact]
    public async Task When_condition_field_matches_hide_value_watermark_is_hidden()
    {
        var options = Options.Create(new WatermarkPolicyOptions
        {
            Enabled = true,
            Field = "xm",
            ConditionField = "sh_flag",
            HideWhenValue = "1" // 审核状态为 1（已审核）时隐藏水印
        });
        var resolver = new DefaultWatermarkResolver(options, _hospitalProvider);

        var definition = CreateDefinition("jktjbbd");

        // 未审核报告（sh_flag = 0）：显示水印
        var dataNotAudited = CreateReportData(("xm", "张三"), ("sh_flag", "0"));
        var resultNotAudited = await resolver.ResolveAsync(definition, new ReportRenderRequest("jktjbbd", new Dictionary<string, JsonElement>()), dataNotAudited, CancellationToken.None);
        Assert.True(resultNotAudited.Enabled);
        Assert.Equal("张三", resultNotAudited.Text);

        // 已审核报告（sh_flag = 1）：隐藏水印
        var dataAudited = CreateReportData(("xm", "张三"), ("sh_flag", "1"));
        var resultAudited = await resolver.ResolveAsync(definition, new ReportRenderRequest("jktjbbd", new Dictionary<string, JsonElement>()), dataAudited, CancellationToken.None);
        Assert.False(resultAudited.Enabled);
    }

    [Fact]
    public async Task When_static_text_configured_it_takes_precedence()
    {
        var options = Options.Create(new WatermarkPolicyOptions
        {
            Enabled = true,
            Field = "xm",
            Text = "内部预览草稿"
        });
        var resolver = new DefaultWatermarkResolver(options, _hospitalProvider);

        var definition = CreateDefinition("jktjbbd");
        var data = CreateReportData(("xm", "张三"));

        var result = await resolver.ResolveAsync(definition, new ReportRenderRequest("jktjbbd", new Dictionary<string, JsonElement>()), data, CancellationToken.None);
        Assert.True(result.Enabled);
        Assert.Equal("内部预览草稿", result.Text);
    }

    [Fact]
    public async Task When_template_configured_formats_text_with_placeholders()
    {
        var options = Options.Create(new WatermarkPolicyOptions
        {
            Enabled = true,
            Field = "xm",
            Template = "{Field} - {HospitalName} 仅供核对"
        });
        var resolver = new DefaultWatermarkResolver(options, _hospitalProvider);

        var definition = CreateDefinition("jktjbbd");
        var data = CreateReportData(("xm", "赵六"));

        var result = await resolver.ResolveAsync(definition, new ReportRenderRequest("jktjbbd", new Dictionary<string, JsonElement>()), data, CancellationToken.None);
        Assert.True(result.Enabled);
        Assert.Equal("赵六 - 中心人民医院 仅供核对", result.Text);
    }

    [Fact]
    public async Task When_per_report_field_override_configured_uses_specific_field()
    {
        var policy = new WatermarkPolicyOptions
        {
            Enabled = true,
            Field = "xm"
        };
        policy.ReportFields["special_report"] = "company_name";

        var resolver = new DefaultWatermarkResolver(Options.Create(policy), _hospitalProvider);

        var definition = CreateDefinition("special_report");
        var data = CreateReportData(("xm", "张三"), ("company_name", "科技有限公司"));

        var result = await resolver.ResolveAsync(definition, new ReportRenderRequest("special_report", new Dictionary<string, JsonElement>()), data, CancellationToken.None);
        Assert.True(result.Enabled);
        Assert.Equal("科技有限公司", result.Text);
    }

    [Fact]
    public async Task When_request_explicitly_disables_watermark_it_overrides_configuration()
    {
        var options = Options.Create(new WatermarkPolicyOptions
        {
            Enabled = true,
            Field = "xm"
        });
        var resolver = new DefaultWatermarkResolver(options, _hospitalProvider);

        var definition = CreateDefinition("jktjbbd");
        var request = new ReportRenderRequest("jktjbbd", new Dictionary<string, JsonElement>(), Watermark: new WatermarkOptions(Enabled: false));
        var data = CreateReportData(("xm", "张三"));

        var result = await resolver.ResolveAsync(definition, request, data, CancellationToken.None);
        Assert.False(result.Enabled);
    }

    private static ReportDefinition CreateDefinition(string reportId) =>
        new(reportId, "1", "tpl", "sql", new Dictionary<string, string>(), DateTimeOffset.UtcNow, "test");

    private static ReportDataSet CreateReportData(params (string ColumnName, string Value)[] values)
    {
        var table = new DataTable("Master");
        foreach (var (col, _) in values)
        {
            table.Columns.Add(col, typeof(string));
        }

        var rowValues = values.Select(v => (object)v.Value).ToArray();
        table.Rows.Add(rowValues);

        var tables = new Dictionary<string, DataTable>(StringComparer.OrdinalIgnoreCase)
        {
            ["Master"] = table
        };
        return new ReportDataSet(tables, 1);
    }

    private sealed class StubWatermarkTextProvider(string hospitalName) : IWatermarkTextProvider
    {
        public Task<string?> GetWatermarkTextAsync(CancellationToken cancellationToken)
            => Task.FromResult<string?>(hospitalName);
    }
}
