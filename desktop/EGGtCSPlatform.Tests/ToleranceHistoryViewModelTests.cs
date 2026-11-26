using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.ViewModels.Pages;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class ToleranceHistoryViewModelTests
{
    [Fact]
    public async Task PageSizeChangesResetPageAndForwardActualGroupPageSize()
    {
        var store = new Store();
        using var model = new ToleranceHistoryViewModel(store);
        await model.LoadAsync();
        Assert.Equal(new[] { 10, 20, 50 }, model.PageSizes);
        Assert.Equal(10, model.Groups.Count);
        Assert.False(model.CanPrevious);
        await model.NextCommand.ExecuteAsync(null);
        Assert.Equal(2, model.Page);
        Assert.Equal(2, store.Requests[^1].Page);

        model.PageSize = 20;
        Assert.Equal(1, model.Page);
        Assert.Equal(20, store.Requests[^1].PageSize);
        Assert.Equal(20, model.Groups.Count);
        Assert.Contains("每页 20 组", model.PageText);
        await model.NextCommand.ExecuteAsync(null);
        await model.NextCommand.ExecuteAsync(null);
        Assert.Equal(3, model.Page);
        Assert.Equal(13, model.Groups.Count);
        Assert.False(model.CanNext);
        var calls = store.Requests.Count;
        await model.NextCommand.ExecuteAsync(null);
        Assert.Equal(calls, store.Requests.Count);

        model.PageSize = 50;
        Assert.Equal(1, model.Page);
        Assert.Equal(50, model.Groups.Count);
        await model.NextCommand.ExecuteAsync(null);
        Assert.Equal(3, model.Groups.Count);
        model.PageSize = 0;
        Assert.Equal(50, model.PageSize);
    }

    [Fact]
    public async Task SearchDatesAndResetPreserveQuerySemantics()
    {
        var store = new Store();
        using var model = new ToleranceHistoryViewModel(store);
        model.PageSize = 20;
        await model.NextCommand.ExecuteAsync(null);
        model.SubjectId = "SUB-001";
        model.SelectedKind = model.Kinds.Single(k => k.Kind == StimulusKind.TAcs);
        model.StartDate = new DateTime(2026, 10, 1);
        model.EndDate = new DateTime(2026, 10, 30);
        await model.SearchCommand.ExecuteAsync(null);
        Assert.Equal(
            new ToleranceHistoryQuery(
                "SUB-001",
                StimulusKind.TAcs,
                new(2026, 10, 1),
                new(2026, 10, 30),
                1,
                20
            ),
            store.Requests[^1]
        );

        var calls = store.Requests.Count;
        model.EndDate = new DateTime(2026, 9, 30);
        await model.SearchCommand.ExecuteAsync(null);
        Assert.True(model.HasError);
        Assert.Contains("开始日期不能晚于结束日期", model.StatusText);
        Assert.Equal(calls, store.Requests.Count);

        await model.ResetCommand.ExecuteAsync(null);
        Assert.Equal(new ToleranceHistoryQuery(), store.Requests[^1]);
        Assert.False(model.HasError);
        Assert.Equal("", model.SubjectId);
        Assert.Null(model.SelectedKind.Kind);
        Assert.Null(model.StartDate);
        Assert.Null(model.EndDate);
    }

    [Fact]
    public async Task StaleRequestsAndDisposedPageCannotReplaceNewerResults()
    {
        var store = new Store { Deferred = true };
        using var model = new ToleranceHistoryViewModel(store);
        var first = model.LoadAsync();
        Assert.True(model.IsLoading);
        Assert.False(model.CanNext);
        model.SubjectId = "new";
        var second = model.SearchCommand.ExecuteAsync(null);
        Assert.True(store.Tokens[0].IsCancellationRequested);
        store.Pending[1].SetResult(new([Group("new")], 1));
        await second;
        Assert.False(model.IsLoading);
        store.Pending[0].SetResult(new([Group("old")], 1));
        await first;
        Assert.Equal("new", Assert.Single(model.Groups).SubjectId);
        var last = model.LoadAsync();
        model.Dispose();
        Assert.True(store.Tokens[2].IsCancellationRequested);
        store.Pending[2].SetResult(new([Group("disposed")], 1));
        await last;
        Assert.Equal("new", Assert.Single(model.Groups).SubjectId);
        Assert.False(model.CanNext);
    }

    [Fact]
    public async Task EmptyErrorRetryAndExpansionArePresentationStateOnly()
    {
        var store = new Store { Failure = new IOException("读取测试失败") };
        using var model = new ToleranceHistoryViewModel(store);
        await model.LoadAsync();
        Assert.True(model.HasError);
        Assert.False(model.IsLoading);
        store.Failure = null;
        store.Total = 0;
        await model.LoadAsync();
        Assert.False(model.HasError);
        Assert.False(model.HasGroups);
        Assert.Equal("暂无耐受度测试记录", model.StatusText);
        Assert.False(model.CanNext);
        Assert.False(model.CanPrevious);
        store.Total = 1;
        await model.LoadAsync();
        var group = Assert.Single(model.Groups);
        var snapshotId = group.Latest.Id;
        Assert.False(group.IsExpanded);
        group.ToggleExpandedCommand.Execute(null);
        Assert.True(group.IsExpanded);
        Assert.Equal(snapshotId, group.Latest.Id);
        Assert.Equal(new[] { "FC5·C", "P3·A" }, group.ElectrodePoints.Select(p => p.Name));
        group.ToggleExpandedCommand.Execute(null);
        Assert.False(group.IsExpanded);
    }

    private static ToleranceHistoryGroup Group(string subject)
    {
        var target = Guid.NewGuid();
        var configuration = new StimulusConfigurationSnapshot(
            StimulusKind.TDcs,
            StimulusArrayMode.DualChannel,
            StimulusDirection.Positive,
            ShamWaveformMode.Direct,
            15,
            40,
            79,
            [new(target, 1, .04, 1, [])]
        );
        var route = new ToleranceTestRouteData(
            new(subject, "备注"),
            configuration,
            [
                new("FC5", target, 1, StimulationChannelRole.FixedActive),
                new("P3", target, 2, StimulationChannelRole.Selectable),
            ],
            []
        );
        return new(
            Guid.NewGuid(),
            subject,
            [
                new(
                    Guid.NewGuid(),
                    DateTimeOffset.Now,
                    1,
                    "operator",
                    .08,
                    true,
                    route,
                    configuration
                ),
            ]
        );
    }

    private sealed class Store : ITolerancePersistenceService
    {
        public int Total = 53;
        public bool Deferred;
        public Exception? Failure;
        public List<ToleranceHistoryQuery> Requests { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public List<TaskCompletionSource<ToleranceHistoryPage>> Pending { get; } = [];

        public Task<ToleranceHistoryPage> QueryAsync(
            ToleranceHistoryQuery query,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(query);
            Tokens.Add(cancellationToken);
            if (Deferred)
            {
                var pending = new TaskCompletionSource<ToleranceHistoryPage>();
                Pending.Add(pending);
                return pending.Task;
            }
            if (Failure is not null)
                return Task.FromException<ToleranceHistoryPage>(Failure);
            return Task.FromResult(
                new ToleranceHistoryPage(
                    Enumerable
                        .Range(0, Total)
                        .Skip((query.Page - 1) * query.PageSize)
                        .Take(query.PageSize)
                        .Select(i => Group($"SUB-{i:D3}"))
                        .ToArray(),
                    Total
                )
            );
        }

        public Task<ToleranceResultSnapshot> SaveAsync(
            ToleranceTestRouteData route,
            double current,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException("历史页不应写入数据。");
    }
}
