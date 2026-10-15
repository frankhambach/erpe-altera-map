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

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 1.0, 1.0);

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

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 1.0, 1.0);

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

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 1.0, 1.0);

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

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 1.0, 1.0);

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

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 1.0, 1.0);

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

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 1.0, 1.0);

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

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 1.0, 1.0);

        arcGraph.Arcs.SelectMany(arc => arc.Coordinates.ToCoordinateArray())
            .Should()
            .OnlyContain(coordinate => (Math.Abs(coordinate.Y) < 1E-9) || (Math.Abs(coordinate.Y - 0.3) < 1E-9));
    }

    [AutoCustomizedData]
    [Test]
    public void Weld_ShouldStillSplit_WhenTheEndIsBeyondTheMergeToleranceButNotTheSplitOne(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The point of the two tolerances being different: an end two units out is a T
        // junction for splitting to take, and splitting may reach it even where merging no
        // longer would, because splitting moves no vertex that is already there.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (10, 0), (20, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(10, 2), (10, 20)]);

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 0.5, 3.0);

        arcGraph.Arcs.Should().HaveCount(3, "the base is split where the stem reaches it");
        Arc stem = arcGraph.Arcs.Single(arc => Array.Exists(arc.GetFullCoordinates(), c => c.Y >= 20.0));
        Node junction = stem.StartNode;
        arcGraph.Arcs.Count(arc => (arc.StartNode == junction) || (arc.EndNode == junction))
            .Should()
            .Be(3, "both halves of the split arc and the stem meet there");
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

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 1.0, 1.0);

        arcGraph.Arcs.SelectMany(
                arc => arc.GetFullCoordinates()
                    .Zip(arc.GetFullCoordinates().Skip(1), (first, second) => first.Equals2D(second)))
            .Should()
            .OnlyContain(repeated => !repeated);
    }

    [AutoCustomizedData]
    [Test]
    public void Weld_ShouldKeepTheVertexPastIt_WhenAnEndLandsOnAVertexOfTheArc(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The split node stands for the vertex at the far end of the segment it splits, which
        // is how a weld pulls an arc onto the end it takes. An end landing on a vertex has
        // nothing to pull, so it has to be expressed against the segment that ends there -
        // written against the one that starts there instead, it stands for the next vertex
        // along and that vertex is lost.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (10, 0), (15, 8), (30, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(10, 0), (10, 20)]);

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 1.0, 1.0);

        arcGraph.Arcs.Should().HaveCount(3, "the arc is cut in two where the stem lands on it");
        arcGraph.Arcs.SelectMany(arc => arc.GetFullCoordinates())
            .Should()
            .Contain(
                coordinate => coordinate.Equals2D(new Coordinate(15.0, 8.0)),
                "the vertex past the split is not the weld's to take");
    }

    [AutoCustomizedData]
    [Test]
    public void Weld_ShouldSplitTheLowerArc_WhenTwoArcsAreTheSameDistanceFromTheEnd(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // Two arcs crossing at a vertex they both carry are both at no distance at all from an
        // end sitting there, so the choice has to come from the arcs rather than from the order
        // the segment index hands them back in - that order changes whenever an arc is added
        // anywhere on the map.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (10, 0), (20, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(10, -10), (10, 0), (10, 10)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(10, 0), (5, 5)]);

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 1.0, 1.0);

        arcGraph.Arcs.Should().HaveCount(4, "only one of the two is cut");
        arcGraph.Arcs.Should()
            .ContainSingle(arc => arc.GetFullCoordinates().Length == 3)
            .Which.GetFullCoordinates()[0]
            .Equals2D(new Coordinate(10.0, -10.0))
            .Should()
            .BeTrue("the arc that keeps all three coordinates is the one added second");
    }

    [AutoCustomizedData]
    [Test]
    public void Weld_ShouldDropTheStubAndSplitForItsNeighbour_WhenAStubCollapses(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The stub is shorter than the tolerance, so its own two ends weld to each other and
        // it becomes a point. A border reaching for the same place joins that group too, and
        // its end then sits on a node referenced three times - twice by the stub - which is no
        // longer a loose end, so nothing welds the border to the long arc 0.9 units away. The
        // stub is dropped in Phase 4 for having no direction, and the border is left dividing
        // nothing. Ligury and Tuscany came out as one face that way.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (20, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(10, 0.9), (10.3, 0.9)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(10.1, 1.0), (10, 10)]);

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 0.5, 3.0);

        // The stub is gone, and the long arc has been split in two to take the border's end.
        arcGraph.Arcs.Should().HaveCount(3);
        arcGraph.Arcs.Should().NotContain(arc => arc.StartNode == arc.EndNode);
        Arc border = arcGraph.Arcs.Single(arc => arc.EndNode.Coordinate.Equals2D(new Coordinate(10, 10)));
        arcGraph.Arcs.Where(arc => arc != border)
            .Should()
            .Contain(arc => arc.StartNode == border.StartNode)
            .And.Contain(arc => arc.EndNode == border.StartNode);
    }

    [AutoCustomizedData]
    [Test]
    public void Weld_ShouldDropTheStubAndSplitForItsNeighbour_WhenACollapsedStubKeepsAVertexOfItsOwn(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The same defect as above, except that the stub has a vertex between its two ends. The
        // merge brings the ends together onto a node the middle vertex is not at, so the stub is
        // collapsed in every sense that matters - it runs out 0.07 units and straight back - and
        // yet no coordinate of it equals its node. A test asking for that misses it, the border
        // stays hanging off a node referenced three times, and Prabang and Vantong come out as
        // one face.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (20, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(10, 0.9), (10.05, 0.97), (10.1, 0.9)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(10, 0.9), (10, 10)]);

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 0.5, 3.0);

        // The stub is gone, and the long arc has been split in two to take the border's end.
        arcGraph.Arcs.Should().HaveCount(3);
        arcGraph.Arcs.Should().NotContain(arc => arc.StartNode == arc.EndNode);
        Arc border = arcGraph.Arcs.Single(arc => arc.EndNode.Coordinate.Equals2D(new Coordinate(10, 10)));
        arcGraph.Arcs.Where(arc => arc != border)
            .Should()
            .Contain(arc => arc.StartNode == border.StartNode)
            .And.Contain(arc => arc.EndNode == border.StartNode);
    }

    [AutoCustomizedData]
    [Test]
    public void Weld_ShouldKeepTheRing_WhenACoastRingIsSmallerThanTheTolerance(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The limit of the rule above: it reads tracers and nothing else. An island's coast comes
        // out of the union already closed, so the merge never touched it, and on this map 1326 of
        // them enclose less than the tolerance would allow a tracer to - the smallest reaching
        // 0.045 units from its node. Their size says nothing about whether they belong.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        arcGraph.AddArc(
            ArcKind.Coast,
            coordinateSequenceFactory.Create(
                [new Coordinate(0, 0), new Coordinate(0.2, 0), new Coordinate(0.2, 0.2), new Coordinate(0, 0)]));

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 0.5, 3.0);

        Arc island = arcGraph.Arcs.Should().ContainSingle().Subject;
        island.StartNode.Should().BeSameAs(island.EndNode);
    }

    [AutoCustomizedData]
    [Test]
    public void Weld_ShouldStillCloseARing_WhenAnArcLongEnoughHasItsOwnEndsMerged(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The other side of the same rule: an arc whose two ends are brought together but
        // which has real length between them has been closed into a ring, not collapsed, and
        // has to stay.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (10, 0), (10, 10), (0, 10), (0.4, 0.2)]);

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 1.0, 1.0);

        Arc ring = arcGraph.Arcs.Should().ContainSingle().Subject;
        ring.StartNode.Should().BeSameAs(ring.EndNode);
    }

    [AutoCustomizedData]
    [Test]
    public void Weld_ShouldCloseTheLoop_WhenAnArcComesBackToItsOwnBody(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // One tracer that starts away from everything, goes right round a region and comes back
        // to within 0.3 of where it set out. Excluding the whole arc from the search, as this
        // did, leaves the end loose and the loop never closes, so the border divides nothing.
        // That is how Kovenda and Bopedi came out as one face.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(
            arcGraph,
            coordinateSequenceFactory,
            [(5, -5), (0, 0), (0, 10), (10, 10), (10, 0), (0.3, 0.1)]);

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 0.1, 1.0);

        arcGraph.Arcs.Should().HaveCount(2);
        arcGraph.Arcs.Should()
            .ContainSingle(arc => arc.StartNode == arc.EndNode, "the loop is closed")
            .And.ContainSingle(arc => arc.StartNode != arc.EndNode, "the tail to it is not");
    }

    [AutoCustomizedData]
    [Test]
    public void Weld_ShouldLeaveTheEndLoose_WhenOnlyItsOwnNeighbourhoodIsNear(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The other half of the same rule. This end is 0.2 from its own arc, but only 0.6 back
        // along it, so what it has found is the stretch it has just come from. Welding there
        // would split the arc beside its own end and leave a stub, which is how stubs are made.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (0.5, 0), (0.5, 0.2), (0.1, 0.2)]);

        CreateService(coordinateSequenceFactory).Weld(arcGraph, 0.1, 1.0);

        Arc arc = arcGraph.Arcs.Should().ContainSingle().Subject;
        arc.StartNode.Should().NotBeSameAs(arc.EndNode);
    }

    [AutoCustomizedData]
    [Test]
    public void Weld_ShouldThrowArgumentNullException_WhenArcGraphIsNull(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        Action action = () => CreateService(coordinateSequenceFactory).Weld(null!, 1.0, 1.0);

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
