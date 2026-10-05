// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Tests.Services;

using System;
using System.Linq;

using Erpe.Altera.Map.Models;
using Erpe.Altera.Map.Services;
using Erpe.Testing;

using FluentAssertions;

using NetTopologySuite.Geometries;

using NUnit.Framework;

[TestFixture]
[TestOf(typeof(CorrectionService))]
public class CorrectionServiceWeldTests
{
    [AutoCustomizedData]
    [Test]
    public void Weld_ShouldMergeEndsOntoOneNode_WhenTheyAreWithinTheTolerance(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (10, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(10.4, 0), (20, 0)]);

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 1.0);

        arcGraph.Arcs.Should().HaveCount(2);
        arcGraph.Arcs[0].EndNode.Should().BeSameAs(arcGraph.Arcs[1].StartNode);
        arcGraph.Arcs[0].EndNode.Coordinate.X.Should().BeApproximately(10.2, 1E-9);
    }

    [AutoCustomizedData]
    [Test]
    public void Weld_ShouldResolveChains_WhenProximityIsNotTransitive(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // A is within tolerance of B and B of C, but A is not within tolerance of C.
        // Pairwise snapping would leave the group half joined; union-find must not.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(-10, 0), (0, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(0.8, 0), (-10, 10)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(1.6, 0), (-10, 20)]);

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 1.0);

        arcGraph.Arcs[0].EndNode.Should().BeSameAs(arcGraph.Arcs[1].StartNode);
        arcGraph.Arcs[1].StartNode.Should().BeSameAs(arcGraph.Arcs[2].StartNode);
        arcGraph.Arcs[0].EndNode.Coordinate.X.Should().BeApproximately(0.8, 1E-9);
    }

    [AutoCustomizedData]
    [Test]
    public void Weld_ShouldSplitTheArc_WhenAnEndLandsPartwayAlongIt(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The dominant case in the real map: a T junction rather than two ends meeting.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (10, 0), (20, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(5, 0.4), (5, 20)]);

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 1.0);

        arcGraph.Arcs.Should().HaveCount(3, "the crossed arc is split in two");
        Arc stem = arcGraph.Arcs.Single(arc => Array.Exists(arc.GetFullCoordinates(), c => c.Y >= 20.0));
        Node junction = stem.StartNode;
        arcGraph.Arcs.Count(arc => (arc.StartNode == junction) || (arc.EndNode == junction))
            .Should()
            .Be(3, "both halves of the split arc and the stem meet at the junction");
    }

    [AutoCustomizedData]
    [Test]
    public void Weld_ShouldSplitAClosedArcIntoTwo_WhenAnEndLandsOnIt(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // A coast ring is a closed arc; a border reaching the sea has to open it up.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (20, 0), (20, 20), (0, 20), (0, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(10, 0.3), (10, -20)]);

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 1.0);

        arcGraph.Arcs.Should().HaveCount(3);
        arcGraph.Arcs.Should().NotContain(arc => arc.IsClosed, "the ring is now open at the junction");
    }

    [AutoCustomizedData]
    [Test]
    public void Weld_ShouldSplitOnce_WhenTwoEndsLandOnTheSameSegment(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (30, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(10, 0.2), (10, 20)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(20, 0.2), (20, 20)]);

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 1.0);

        arcGraph.Arcs.Should().HaveCount(5, "the base is cut into three pieces by two stems");
    }

    [AutoCustomizedData]
    [Test]
    public void Weld_ShouldLeaveTheEndLoose_WhenNothingIsWithinTheTolerance(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (10, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(14, 0), (20, 0)]);

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 1.0);

        arcGraph.Arcs.Should().HaveCount(2);
        arcGraph.Arcs[0].EndNode.Should().NotBeSameAs(arcGraph.Arcs[1].StartNode);
    }

    [AutoCustomizedData]
    [Test]
    public void Weld_ShouldNotMoveInteriorVertices_WhenTheyAreWithinTheTolerance(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The tolerance is for ends only. Interior linework is not defective, and pulling
        // it about would staircase genuine coastline.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (10, 0), (20, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0.3), (10, 0.3), (20, 0.3)]);

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 1.0);

        arcGraph.Arcs.SelectMany(arc => arc.Coordinates.ToCoordinateArray())
            .Should()
            .OnlyContain(coordinate => (Math.Abs(coordinate.Y) < 1E-9) || (Math.Abs(coordinate.Y - 0.3) < 1E-9));
    }

    [AutoCustomizedData]
    [Test]
    public void Weld_ShouldNotRepeatAVertex_WhenAnArcIsSplit(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The piece after a split ends at its end node, which already stands for the arc's
        // last vertex. Carrying that vertex as geometry as well leaves every split arc with
        // a zero length segment on the end.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (10, 0), (20, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(5, 0.4), (5, 20)]);

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 1.0);

        arcGraph.Arcs.SelectMany(
                arc => arc.GetFullCoordinates()
                    .Zip(arc.GetFullCoordinates().Skip(1), (first, second) => first.Equals2D(second)))
            .Should()
            .OnlyContain(repeated => !repeated);
    }

    [AutoCustomizedData]
    [Test]
    public void Weld_ShouldThrowArgumentNullException_WhenArcGraphIsNull(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        Action action = () => CreateService(coordinateSequenceFactory).Weld(null!, 1.0);

        action.Should().Throw<ArgumentNullException>();
    }

    private static void AddArc(
        ArcGraph arcGraph,
        CoordinateSequenceFactory coordinateSequenceFactory,
        (double X, double Y)[] points)
    {
        arcGraph.AddArc(
            ArcKind.Border,
            coordinateSequenceFactory.Create(points.Select(point => new Coordinate(point.X, point.Y)).ToArray()));
    }

    private static ArcGraph CreateGraph(CoordinateSequenceFactory coordinateSequenceFactory)
    {
        return new ArcGraph(coordinateSequenceFactory);
    }

    private static CorrectionService CreateService(CoordinateSequenceFactory coordinateSequenceFactory)
    {
        return new CorrectionService(new GeometryFactory(new PrecisionModel(10000.0), 0, coordinateSequenceFactory));
    }
}
