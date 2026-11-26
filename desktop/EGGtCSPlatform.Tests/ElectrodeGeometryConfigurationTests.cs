using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using EGGtCSPlatform.Configuration;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class ElectrodeGeometryConfigurationTests
{
    [Fact]
    public void MigrationPreservesCatalogAndWiringAndOnlyAddsGeometryById()
    {
        using var files = new ConfigFiles();
        var original = JsonNode.Parse("""
        { "_meta":{"schemaVersion":1}, "ElectrodePositions":{"Positions":[
          {"Id":"custom","Position":"自定义","ReferenceX":12,"ReferenceY":20},
          {"Id":"f3","Position":"左额叶","ReferenceX":150,"ReferenceY":123},
          {"Id":"F4","Position":"右额叶","ReferenceX":350,"ReferenceY":123,"Position3D":{"X":40,"Y":30,"Z":50}}
        ]}, "EegAcquisitionChannels":{"PhysicalChannelCount":32,"ElectrodeMappings":[{"ElectrodeId":"f3","PhysicalChannel":9}]} }
        """)!.AsObject();
        files.Write(original);
        files.Sync();
        var migrated = files.Read();
        var positions = migrated["ElectrodePositions"]!["Positions"]!.AsArray();
        Assert.Equal(3, positions.Count);
        Assert.Null(positions[0]!["Position3D"]);
        Assert.Null(positions[0]!["DefaultRole"]);
        Assert.Equal("左额叶", positions[1]!["Position"]!.GetValue<string>());
        Assert.Equal(150, positions[1]!["ReferenceX"]!.GetValue<int>());
        Assert.True(positions[1]!["Position3D"]!["X"]!.GetValue<double>() < 0);
        Assert.Equal(40, positions[2]!["Position3D"]!["X"]!.GetValue<int>());
        Assert.Null(positions[2]!["Normal3D"]);
        Assert.Equal(9, migrated["EegAcquisitionChannels"]!["ElectrodeMappings"]![0]!["PhysicalChannel"]!.GetValue<int>());
        Assert.Equal(2, migrated["_meta"]!["schemaVersion"]!.GetValue<int>());
        var backup = Assert.Single(Directory.GetFiles(files.Backups));
        Assert.True(JsonNode.DeepEquals(original, JsonNode.Parse(File.ReadAllText(backup))));
        files.Sync();
        Assert.True(JsonNode.DeepEquals(migrated, files.Read()));
        Assert.Single(Directory.GetFiles(files.Backups));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("not-a-number")]
    public void VersionTwoDoesNotSilentlyRepairMissingOrInvalidGeometry(string invalidCoordinate)
    {
        using var files = new ConfigFiles();
        var config = files.Default.DeepClone().AsObject();
        var points = config["ElectrodePositions"]!["Positions"]!.AsArray();
        while (points.Count > 2) points.RemoveAt(points.Count - 1);
        points[0]!.AsObject().Remove("Position3D");
        points[1]!["Position3D"]!["X"] = invalidCoordinate;
        files.Write(config);
        files.Sync();
        Assert.True(JsonNode.DeepEquals(config, files.Read()));
        var options = ElectrodePositionOptions.Read(new ConfigurationBuilder().AddJsonFile(files.User).Build()
            .GetSection(ElectrodePositionOptions.SectionName));
        using var page = CreatePage(options);
        Assert.Equal(2, page.Points.Count);
        Assert.False(page.CanShowHead3D);
        Assert.Contains("FP1", page.Head3DConfigurationError);
        Assert.Contains("FP2", page.Head3DConfigurationError);
    }

    [Fact]
    public void CatalogSharesIdCountLabelsAndGeometryButNotHardwareChannelCount()
    {
        var options = new ElectrodePositionOptions { Positions = Enumerable.Range(1,40)
            .Select(i => new ElectrodePositionDefinition($"custom-{i}", $"显示 {i}", i, i,
                Position3D:new ElectrodeVector3(i,20,50))).ToArray() };
        using var page = CreatePage(options);
        Assert.True(page.CanShowHead3D);
        Assert.Equal(40,page.Points.Count);
        Assert.Equal("custom-1",page.Points[0].Name);
        Assert.Equal("显示 1",page.Points[0].PositionName);
        Assert.Equal(options.Positions[0].Position3D,page.Points[0].Position3D);
        Assert.Null(page.Points[0].StimulationPhysicalChannelId);
        Assert.Equal(32,EegPhysicalChannelMappingService.SupportedPhysicalChannelCount);
        options.Positions = [options.Positions[0], options.Positions[0] with {Id="CUSTOM-1"}];
        Assert.False(options.IsValid());
    }

    [Theory]
    [InlineData(0,0,0)]
    [InlineData(double.NaN,1,0)]
    [InlineData(double.PositiveInfinity,0,1)]
    public void InvalidNormalDisablesOnlyThreeDimensionalView(double x,double y,double z)
    {
        var options = new ElectrodePositionOptions { Positions = [new("custom","点位",200,200,
            Position3D:new(20,30,40),Normal3D:new(x,y,z))] };
        Assert.True(options.IsValid());
        using var page = CreatePage(options);
        Assert.False(page.CanShowHead3D);
        Assert.Contains("custom",page.Head3DConfigurationError);
        Assert.Single(page.Points);
    }

    private static ElectrodeConfigurationPageViewModel CreatePage(ElectrodePositionOptions options)
    {
        var host = new MainViewModel(new PageFactory((_,_)=>throw new NotSupportedException()),new DeviceSelectionContext());
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs);
        return new(new ElectrodeConfigurationRouteData("test","simulated",stimulus.CreateSnapshot()),
            host,new DialogService(()=>null),host,electrodeCatalog:new ElectrodePositionCatalog(options));
    }

    private sealed class ConfigFiles : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(),"electrode-geometry-"+Guid.NewGuid().ToString("N"));
        public string User => Path.Combine(root,"user.json");
        public string Backups => Path.Combine(root,"backups");
        public JsonObject Default {get;}
        public ConfigFiles()
        {
            Directory.CreateDirectory(root);
            Default=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"appsettings.default.json")))!.AsObject();
            File.WriteAllText(Path.Combine(root,"default.json"),Default.ToJsonString());
        }
        public void Write(JsonObject value)=>File.WriteAllText(User,value.ToJsonString());
        public JsonObject Read()=>JsonNode.Parse(File.ReadAllText(User))!.AsObject();
        public void Sync()=>new ApplicationConfigurationSynchronizer().Synchronize(new(Path.Combine(root,"default.json"),User,Backups));
        public void Dispose()=>Directory.Delete(root,true);
    }
}
