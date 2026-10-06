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
[TestOf(typeof(TopologyService))]
public class TopologyServiceFaceTests
{
    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldTraceOneFace_WhenTheGraphIsASingleRing(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, [(0, 0), (10, 0), (10, 10), (0, 10), (0, 0)]);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0);

        Face face = arcGraph.Faces.Should().ContainSingle().Subject;
        face.Holes.Should().BeEmpty();
        face.Shell.Should().ContainSingle();
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldTraceTwoFaces_WhenARingIsDividedByABorder(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The case the whole pipeline exists for: one coastline cut in two by a border,
        // with both halves referring to the very same border arc.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(0, 0));
        Node east = arcGraph.AddNode(new Coordinate(10, 0));
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0, 10), (10, 10)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0, -10), (10, -10)]);
        Arc border = AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Border, west, east, []);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0);

        arcGraph.Faces.Should().HaveCount(2);
        arcGraph.Faces.Should()
            .OnlyContain(face => face.Shell.Any(directed => directed.Arc == border), "both sides share the arc");
        arcGraph.Faces.Select(face => face.Shell.Single(directed => directed.Arc == border).IsReversed)
            .Should()
            .BeEquivalentTo([true, false], "each side traverses the shared border the opposite way");
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldCarryTheInnerRingAsAHole_WhenOneRingEnclosesAnother(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, [(0, 0), (30, 0), (30, 30), (0, 30), (0, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, [(10, 10), (20, 10), (20, 20), (10, 20), (10, 10)]);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0);

        arcGraph.Faces.Should().HaveCount(2, "the island and the lake inside it");
        Face island = arcGraph.Faces.MaxBy(face => face.Shell.Length == 1 ? 1 : 0)!;
        arcGraph.Faces.Count(face => face.Holes.Length == 1).Should().Be(1, "only the island carries a hole");
        island.Should().NotBeNull();
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldNodeTheCrossing_WhenTwoArcsCrossAwayFromANode(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // A traversal cannot represent a crossing: each arc would be traced as though the
        // other were not there, and the faces around it would overlap.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Border, [(-10, 0), (10, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Border, [(0, -10), (0, 10)]);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0);

        arcGraph.Arcs.Should().HaveCount(4, "both arcs are split at the crossing");
        arcGraph.Arcs.Select(arc => arc.StartNode)
            .Concat(arcGraph.Arcs.Select(arc => arc.EndNode))
            .Where(node => (Math.Abs(node.Coordinate.X) < 1E-9) && (Math.Abs(node.Coordinate.Y) < 1E-9))
            .Distinct()
            .Should()
            .ContainSingle("all four pieces meet at one node at the crossing");
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldPutEveryHalfEdgeInExactlyOneRing_WhenTheGraphIsDivided(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The invariant the traversal rests on. If it ever fails the faces do not tile,
        // whatever the areas happen to add up to.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(0, 0));
        Node east = arcGraph.AddNode(new Coordinate(10, 0));
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0, 10), (10, 10)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0, -10), (10, -10)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Border, west, east, []);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0);

        int inFaces = arcGraph.Faces.Sum(face => face.Shell.Length + face.Holes.Sum(hole => hole.Length));
        int outlines = (arcGraph.Arcs.Count * 2) - inFaces;
        outlines.Should().BeGreaterThan(0, "the outer boundary is traced too");
        inFaces.Should().Be(4, "three arcs give six half-edges, four of them in the two bounded faces");
        outlines.Should().Be(2, "the remaining two trace the outer boundary");
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldMergeTheNodes_WhenTwoOfThemShareACoordinate(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // Each arc brings its own nodes, so two arcs that begin at the same point begin at
        // two different nodes which happen to sit on top of each other. They are one point
        // and the traversal has to see them as one.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(
            arcGraph,
            coordinateSequenceFactory,
            ArcKind.Coast,
            [(0, 0), (30, 0), (30, 30), (0, 30), (0, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Border, [(0, 0), (-30, 0)]);
        arcGraph.Nodes.Count.Should().Be(3, "the ring brings one node and the spur two");

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0);

        arcGraph.Nodes
            .GroupBy(node => (node.Coordinate.X, node.Coordinate.Y))
            .Should()
            .OnlyContain(grouping => grouping.Count() == 1, "no coordinate carries two nodes any more");
        arcGraph.Arcs.Should().HaveCount(2, "both arcs have length and stay");
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldDropTheArc_WhenItHasNoLength(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // An arc every one of whose coordinates is the same point has no direction to be
        // traced by, and GetDepartureAngle can only answer zero for it, which is enough to
        // send the traversal the wrong way round the node.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(
            arcGraph,
            coordinateSequenceFactory,
            ArcKind.Coast,
            [(0, 0), (30, 0), (30, 30), (0, 30), (0, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Border, [(15, 15), (15, 15)]);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0);

        arcGraph.Arcs.Should().ContainSingle("the ring stays and the arc with no length goes");
        arcGraph.Arcs[0].Kind.Should().Be(ArcKind.Coast);
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldStillTraceTheFace_WhenItComesToLessThanTheCoastlineEncloses(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The shortfall is reported rather than thrown, so a run that loses ground still
        // produces its output and says so. Here the one face covers 900 of the 2000 units
        // claimed, which is the shape of the real defect: land inside no face at all.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(
            arcGraph,
            coordinateSequenceFactory,
            ArcKind.Coast,
            [(0, 0), (30, 0), (30, 30), (0, 30), (0, 0)]);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 2000.0);

        arcGraph.Faces.Should().ContainSingle();
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldDropBorder_WhenItIsTheCoastDrawnASecondTime(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // A border cannot divide land from sea, so one joining the same two nodes as a coast
        // arc and running along it is a second copy of the shore. Left in, both copies leave
        // each of the two nodes, the four half-edges interleave, and a traversal arriving
        // along the coast takes the border as its next turn and walks inland.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(0, 0));
        Node east = arcGraph.AddNode(new Coordinate(10, 0));
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(5.0, 0.02)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, 10.0), (10.0, 10.0)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Border, west, east, []);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0);

        arcGraph.Arcs.Should().NotContain(arc => arc.Kind == ArcKind.Border);
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldKeepBorder_WhenItTakesItsOwnRouteBetweenTheSameNodes(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // Two units from the shore is a route of its own rather than a copy of one, and it
        // divides the ground either side of it. The threshold is a tenth of a unit, which
        // RemoveCoastDuplicates measures.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(0, 0));
        Node east = arcGraph.AddNode(new Coordinate(10, 0));
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(5.0, 2.0)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, 10.0), (10.0, 10.0)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Border, west, east, []);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0);

        arcGraph.Arcs.Should().Contain(arc => arc.Kind == ArcKind.Border);
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldThrowArgumentNullException_WhenArcGraphIsNull(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        Action action = () => CreateService(coordinateSequenceFactory).BuildFaces(null!, 0.0);

        action.Should().Throw<ArgumentNullException>();
    }

    private static void AddArc(
        ArcGraph arcGraph,
        CoordinateSequenceFactory coordinateSequenceFactory,
        ArcKind kind,
        (double X, double Y)[] points)
    {
        arcGraph.AddArc(kind, Create(coordinateSequenceFactory, points));
    }

    private static Arc AddArc(
        ArcGraph arcGraph,
        CoordinateSequenceFactory coordinateSequenceFactory,
        ArcKind kind,
        Node startNode,
        Node endNode,
        (double X, double Y)[] interior)
    {
        return arcGraph.AddArc(kind, startNode, endNode, Create(coordinateSequenceFactory, interior));
    }

    private static CoordinateSequence Create(
        CoordinateSequenceFactory coordinateSequenceFactory,
        (double X, double Y)[] points)
    {
        return coordinateSequenceFactory.Create(
            points.Select(point => new Coordinate(point.X, point.Y)).ToArray());
    }

    private static ArcGraph CreateGraph(CoordinateSequenceFactory coordinateSequenceFactory)
    {
        return new ArcGraph(coordinateSequenceFactory);
    }

    private static TopologyService CreateService(CoordinateSequenceFactory coordinateSequenceFactory)
    {
        return new TopologyService(new GeometryFactory(new PrecisionModel(10000.0), 0, coordinateSequenceFactory));
    }
}
