// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Erpe.Altera.Map.Models;
using Erpe.Altera.Map.Services;
using Erpe.Testing;

using FluentAssertions;

using NetTopologySuite.Geometries;

using NUnit.Framework;

[TestFixture]
[TestOf(typeof(TopoJsonService))]
public class TopoJsonServiceTests
{
    [AutoCustomizedData]
    [Test]
    public async Task WriteAsync_ShouldNameASharedArcOnceAndReferToItFromBothSides(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The whole reason for the format. The divider is the border between the two countries,
        // and it is written once: one of them refers to it forwards and the other backwards, so
        // there is no second copy of the line to disagree with the first.
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(0, 0));
        Node east = arcGraph.AddNode(new Coordinate(10, 0));
        Arc north = AddArc(arcGraph, coordinateSequenceFactory, west, east, [(0.0, 10.0), (10.0, 10.0)]);
        Arc south = AddArc(arcGraph, coordinateSequenceFactory, west, east, [(0.0, -10.0), (10.0, -10.0)]);
        Arc divider = AddArc(arcGraph, coordinateSequenceFactory, west, east, [(5.0, 0.0)]);

        JsonElement topology = await WriteAsync(
                arcGraph,
                [
                    ("countries",
                        [
                            CreateCountry("North", [[new DirectedArc(north, false), new DirectedArc(divider, true)]]),
                            CreateCountry("South", [[new DirectedArc(divider, false), new DirectedArc(south, true)]]),
                        ]),
                ])
            .ConfigureAwait(false);

        topology.GetProperty("arcs").GetArrayLength().Should().Be(3, "three arcs, each written once");
        int[] references = topology.GetProperty("objects")
            .GetProperty("countries")
            .GetProperty("geometries")
            .EnumerateArray()
            .SelectMany(geometry => geometry.GetProperty("arcs").EnumerateArray())
            .SelectMany(ring => ring.EnumerateArray())
            .Select(index => index.GetInt32())
            .ToArray();
        references.Should().HaveCount(4, "two rings of two arcs each");
        IGrouping<int, int>[] byArc = [.. references.GroupBy(index => index < 0 ? ~index : index)];
        byArc.Should().HaveCount(3, "three arcs are referred to between them");
        IGrouping<int, int> shared = byArc.Should().ContainSingle(group => group.Count() == 2).Subject;
        shared.Count(index => index < 0)
            .Should()
            .Be(1, "the divider is the one arc both refer to, and they walk it opposite ways");
        byArc.Where(group => group.Count() == 1)
            .Should()
            .HaveCount(2, "the two coasts have one side each");
    }

    [AutoCustomizedData]
    [Test]
    public async Task WriteAsync_ShouldQuantiseAndDeltaEncodeTheArcs(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The transform says what the integers mean, and every position after an arc's first is
        // the step from the one before it. Decoding has to give back the coordinates that went in.
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(1.5, 2.25));
        Node east = arcGraph.AddNode(new Coordinate(4.0, 2.25));
        Arc north = AddArc(arcGraph, coordinateSequenceFactory, west, east, [(1.5, 9.125), (4.0, 9.125)]);
        Arc south = AddArc(arcGraph, coordinateSequenceFactory, west, east, [(2.75, 0.5)]);

        JsonElement topology = await WriteAsync(
                arcGraph,
                [
                    ("countries",
                        [CreateCountry("One", [[new DirectedArc(north, false), new DirectedArc(south, true)]])]),
                ])
            .ConfigureAwait(false);

        topology.GetProperty("transform")
            .GetProperty("scale")
            .EnumerateArray()
            .Select(value => value.GetDouble())
            .Should()
            .AllBeEquivalentTo(0.000001);
        topology.GetProperty("transform")
            .GetProperty("translate")
            .EnumerateArray()
            .Select(value => value.GetDouble())
            .Should()
            .AllBeEquivalentTo(0.0);
        Decode(topology, 0)
            .Should()
            .Equal((1.5, 2.25), (1.5, 9.125), (4.0, 9.125), (4.0, 2.25));
        Decode(topology, 1).Should().Equal((1.5, 2.25), (2.75, 0.5), (4.0, 2.25));
    }

    [AutoCustomizedData]
    [Test]
    public async Task WriteAsync_ShouldWindTheShellAgainstTheHole(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The specification asks nothing about winding, so this follows the GeoJson beside it:
        // RFC 7946's counter-clockwise shells and clockwise holes. Both rings are handed over the
        // wrong way round here and both have to come back turned.
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        Arc outside = arcGraph.AddArc(
            ArcKind.Coast,
            Create(coordinateSequenceFactory, [(0, 0), (0, 30), (30, 30), (30, 0), (0, 0)]));
        Arc inside = arcGraph.AddArc(
            ArcKind.Lake,
            Create(coordinateSequenceFactory, [(10, 10), (20, 10), (20, 20), (10, 20), (10, 10)]));

        JsonElement topology = await WriteAsync(
                arcGraph,
                [
                    ("countries",
                        [
                            CreateCountry(
                                "One",
                                [[new DirectedArc(outside, false)], [new DirectedArc(inside, false)]]),
                        ]),
                ])
            .ConfigureAwait(false);

        JsonElement rings = topology.GetProperty("objects")
            .GetProperty("countries")
            .GetProperty("geometries")
            .EnumerateArray()
            .Single()
            .GetProperty("arcs");
        GetSignedArea(Decode(topology, rings[0][0].GetInt32())).Should().BePositive("the shell");
        GetSignedArea(Decode(topology, rings[1][0].GetInt32())).Should().BeNegative("the hole");
    }

    [Test]
    public async Task WriteAsync_ShouldThrowArgumentNullException_WhenTheLayersAreNull()
    {
        Func<Task> action = () =>
            new TopoJsonService().WriteAsync(null!, new Dictionary<Arc, Coordinate[]>(), CreateFile());

        await action.Should().ThrowAsync<ArgumentNullException>().ConfigureAwait(false);
    }

    private static Arc AddArc(
        ArcGraph arcGraph,
        CoordinateSequenceFactory coordinateSequenceFactory,
        Node startNode,
        Node endNode,
        (double X, double Y)[] interior)
    {
        return arcGraph.AddArc(ArcKind.Border, startNode, endNode, Create(coordinateSequenceFactory, interior));
    }

    private static CoordinateSequence Create(
        CoordinateSequenceFactory coordinateSequenceFactory,
        (double X, double Y)[] points)
    {
        return coordinateSequenceFactory.Create(
            points.Select(point => new Coordinate(point.X, point.Y)).ToArray());
    }

    private static Country CreateCountry(string id, DirectedArc[][] rings)
    {
        return new Country(
            id,
            CountryType.UnitaryState,
            [new Shape(id + "-0", Polygon.Empty, rings)]);
    }

    private static FileInfo CreateFile()
    {
        return new FileInfo(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".topojson"));
    }

    // The arc at this index, or its reverse for a negative one, as absolute coordinates.
    private static (double X, double Y)[] Decode(JsonElement topology, int index)
    {
        double scale = topology.GetProperty("transform").GetProperty("scale")[0].GetDouble();
        JsonElement arc = topology.GetProperty("arcs")[index < 0 ? ~index : index];
        List<(double X, double Y)> coordinates = [];
        long x = 0;
        long y = 0;
        foreach (JsonElement position in arc.EnumerateArray())
        {
            x = coordinates.Count == 0 ? position[0].GetInt64() : x + position[0].GetInt64();
            y = coordinates.Count == 0 ? position[1].GetInt64() : y + position[1].GetInt64();
            coordinates.Add((x * scale, y * scale));
        }

        if (index < 0)
        {
            coordinates.Reverse();
        }

        return [.. coordinates];
    }

    private static double GetSignedArea((double X, double Y)[] coordinates)
    {
        double area = 0.0;
        for (int i = 0; i < coordinates.Length; ++i)
        {
            (double X, double Y) current = coordinates[i];
            (double X, double Y) next = coordinates[(i + 1) % coordinates.Length];
            area += (current.X * next.Y) - (next.X * current.Y);
        }

        return area / 2.0;
    }

    private static async Task<JsonElement> WriteAsync(
        ArcGraph arcGraph,
        (string Name, IReadOnlyList<Country> Countries)[] layers)
    {
        FileInfo file = CreateFile();
        try
        {
            await new TopoJsonService().WriteAsync(
                    layers,
                    arcGraph.Arcs.ToDictionary(arc => arc, arc => arc.GetFullCoordinates()),
                    file)
                .ConfigureAwait(false);

            using JsonDocument document = JsonDocument.Parse(
                await File.ReadAllTextAsync(file.FullName).ConfigureAwait(false));

            return document.RootElement.Clone();
        }
        finally
        {
            file.Delete();
        }
    }
}
