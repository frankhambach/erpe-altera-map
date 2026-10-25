// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Tests.Services;

using System;
using System.Collections.Generic;
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

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.0);

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

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.0);

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

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.0);

        arcGraph.Faces.Should().HaveCount(2, "the island and the lake inside it");
        Face island = arcGraph.Faces.MaxBy(face => face.Shell.Length == 1 ? 1 : 0)!;
        arcGraph.Faces.Count(face => face.Holes.Length == 1).Should().Be(1, "only the island carries a hole");
        island.Should().NotBeNull();
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldGiveTheIsletToTheLake_WhenItTouchesTheLakeShoreAtItsNode(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // An islet drawn with its node on a vertex of the lake shore. That vertex lies on the
        // lake's shell and not inside it, so asked of it alone the islet was enclosed only by the
        // island around the lake, and became a hole of the land rather than of the water.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, [(0, 0), (30, 0), (30, 30), (0, 30), (0, 0)]);
        AddArc(
            arcGraph,
            coordinateSequenceFactory,
            ArcKind.Lake,
            [(10, 10), (20, 10), (20, 20), (10, 20), (10, 15), (10, 10)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, [(10, 15), (12, 14), (12, 16), (10, 15)]);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.0);

        Face lake = arcGraph.Faces.Single(face => face.Shell.All(directed => directed.Arc.Kind == ArcKind.Lake));
        lake.Holes.Should()
            .ContainSingle()
            .Which.Should()
            .OnlyContain(directed => directed.Arc.Kind == ArcKind.Coast, "the islet is the lake's");
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

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.0);

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

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.0);

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

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.0);

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

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.0);

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

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 2000.0, 0.0);

        arcGraph.Faces.Should().ContainSingle();
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldDropClosedArc_WhenItIsShorterThanTheNodingGrid(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // A closed arc the noding grid puts entirely at its own node is a point, and a point
        // has no direction for the traversal to leave the node in. Asking for coordinates
        // exactly equal to the node left 18 of these in the sample map. A closed one is simply
        // dropped; an open one has its two nodes brought together first, which the test below
        // covers.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(0, 0));
        Node east = arcGraph.AddNode(new Coordinate(10, 0));
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, 10.0), (10.0, 10.0)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, -10.0), (10.0, -10.0)]);
        Arc speck = AddArc(
            arcGraph,
            coordinateSequenceFactory,
            ArcKind.Border,
            east,
            east,
            [(10.00003, 0.00002)]);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.0);

        arcGraph.Arcs.Should().NotContain(speck).And.HaveCount(2);
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldStillTraceTheFaces_WhenAnArcShorterThanTheNodingGridDanglesFromANode(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The open half of the same rule. An arc that does not reach one grid step from its own
        // start has no length and no direction the grid can represent, and leaving it in place
        // is not harmless: here it hangs off the node where the divider meets the coast, with
        // nothing on its far end, so it is a bridge by itself. The traversal takes it first in
        // the departure order, walks to the dangling node and back, and from there retraces
        // every arc it has already walked in the other direction - one ring of zero signed area
        // bounding nothing, and both faces lost.
        //
        // Contracting it is safe where merging two nodes a grid step apart is not: these two
        // nodes are already joined by this arc, so bringing them together is an edge
        // contraction and cannot change what is connected to what.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(0, 0));
        Node east = arcGraph.AddNode(new Coordinate(10, 0));
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, 10.0), (10.0, 10.0)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, -10.0), (10.0, -10.0)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Substate, west, east, [(5.0, 0.0)]);
        Node dangling = arcGraph.AddNode(new Coordinate(10.00003, 0.00002));
        Arc stub = AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, east, dangling, []);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.0);

        arcGraph.Arcs.Should().NotContain(stub).And.HaveCount(3);
        arcGraph.Nodes.Should().NotContain(dangling);
        arcGraph.Faces.Should().HaveCount(2);
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldKeepBothNodes_WhenAnArcIsLongerThanTheNodingGrid(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The other side of that rule, and the reason it is drawn at the grid rather than
        // anywhere larger. An arc with a length the grid can represent is geometry, however
        // small, and its two ends are two points. Contracting it would move a node that the
        // drawing put somewhere else.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(0, 0));
        Node east = arcGraph.AddNode(new Coordinate(10, 0));
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, 10.0), (10.0, 10.0)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, -10.0), (10.0, -10.0)]);
        Node tip = arcGraph.AddNode(new Coordinate(10.01, 0.0));
        Arc stub = AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, east, tip, []);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.0);

        arcGraph.Arcs.Should().Contain(stub);
        arcGraph.Nodes.Should().Contain(tip).And.Contain(east);
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldDropBorder_WhenItIsTheCoastDrawnASecondTime(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // A border cannot divide land from sea, so one joining the same two nodes as a coast
        // arc and running along it is a second copy of the shore. Left in, both copies leave
        // each of the two nodes, the four half-edges interleave, and a traversal arriving
        // along the coast takes the border as its next turn and walks inland. The coast is
        // the one kept, whichever identifier it carries.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(0, 0));
        Node east = arcGraph.AddNode(new Coordinate(10, 0));
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(5.0, 0.02)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, 10.0), (10.0, 10.0)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Border, west, east, []);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.0);

        arcGraph.Arcs.Should().NotContain(arc => arc.Kind == ArcKind.Border);
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldDropBorder_WhenItIsAnotherBorderDrawnASecondTime(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // Welding attaches a loose end to the arc it lands on, and the piece either side of
        // the junction keeps the drawn vertex next to it, so an end that overshoots leaves a
        // stub doubling back. Where another arc already ran that way the stub is a copy of
        // it, and the two copies reverse the traversal between them. That is Mauretania and
        // Altavia, which came out as one face with the border between them traversed both
        // ways in the same ring.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(0, 0));
        Node east = arcGraph.AddNode(new Coordinate(10, 0));
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Border, west, east, [(5.0, 0.02)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, 10.0), (10.0, 10.0)]);
        Arc second = AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Border, west, east, []);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.0);

        arcGraph.Arcs.Should().NotContain(second).And.Contain(arc => arc.Kind == ArcKind.Border);
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldKeepBorder_WhenItTakesItsOwnRouteBetweenTheSameNodes(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // Two units from the shore is a route of its own rather than a copy of one, and it
        // divides the ground either side of it. What tells them apart is the sliver the two
        // enclose, measured in RemoveDuplicateArcs: this one comes to 0.48 against the 0.005
        // of the copy above.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(0, 0));
        Node east = arcGraph.AddNode(new Coordinate(10, 0));
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(5.0, 2.0)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, 10.0), (10.0, 10.0)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Border, west, east, []);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.0);

        arcGraph.Arcs.Should().Contain(arc => arc.Kind == ArcKind.Border);
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldPruneTheArc_WhenItDanglesAndIsShorterThanTheTolerance(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // An overshoot: two borders that should meet at a point cross a little before their ends,
        // 5a nodes the crossing, and what is left beyond it ends on a node with nothing else on
        // it. It bounds no region on either side, so the traversal walks out and straight back and
        // 5h reports it as separating nothing. Arc 11378 was 0.19 units of exactly this.
        //
        // The other tests here pass a tolerance of zero, so that each stays about the one rule it
        // was written for.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(0, 0));
        Node east = arcGraph.AddNode(new Coordinate(10, 0));
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, 10.0), (10.0, 10.0)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, -10.0), (10.0, -10.0)]);
        Node tip = arcGraph.AddNode(new Coordinate(10.2, 0.0));
        Arc overshoot = AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Border, east, tip, []);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.5);

        arcGraph.Arcs.Should().NotContain(overshoot).And.HaveCount(2);
        arcGraph.Faces.Should().ContainSingle();
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldKeepTheArc_WhenItDanglesButIsLongerThanTheTolerance(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The bound is the whole point of the rule. Below the weld tolerance an overshoot is
        // inside the distance at which phase 3 already calls two drawn ends one point, so there is
        // nothing in it. A longer dangle is a real failure - a chain that never closed, a region
        // that got no face - and pruning it would delete the evidence rather than the defect.
        // Prabang's border dangled for 44 units and reading that report is how it was found.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(0, 0));
        Node east = arcGraph.AddNode(new Coordinate(10, 0));
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, 10.0), (10.0, 10.0)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, -10.0), (10.0, -10.0)]);
        Node tip = arcGraph.AddNode(new Coordinate(14.0, 0.0));
        Arc dangle = AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Border, east, tip, []);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.5);

        arcGraph.Arcs.Should().Contain(dangle);
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldDropTheArc_WhenItComesBackToItsNodeAlongTheSameLine(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // One line drawn twice inside a single arc. 5c sees one line drawn twice as two arcs
        // sharing two nodes; this one shares its node with itself, so 5c cannot reach it.
        //
        // Both of its half-edges leave the node along the same ray, so both get the same
        // departure angle, the sort falls through to the arc identifier - which cannot separate
        // them either, since both are the same arc - and which of the two the traversal turns
        // onto comes down to the order they were added in. There is no ordering them: reading
        // the arc forwards and backwards gives the same sequence, so comparing the two further
        // along, which is what separates two different arcs leaving at one angle, answers
        // "equal" at every vertex. It goes instead.
        //
        // Arc 11183 was 0.00024 units of this at node 363 and arc 11589 0.00057 at node 756,
        // both of them minted by 5a splitting at a crossing.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(0, 0));
        Node east = arcGraph.AddNode(new Coordinate(10, 0));
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, 10.0), (10.0, 10.0)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, -10.0), (10.0, -10.0)]);
        Arc retraced = AddArc(
            arcGraph,
            coordinateSequenceFactory,
            ArcKind.Border,
            east,
            east,
            [(11.0, 0.0)]);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.0);

        arcGraph.Arcs.Should().NotContain(retraced).And.HaveCount(2);
        arcGraph.Faces.Should().ContainSingle();
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldKeepTheArc_WhenItComesBackToItsNodeByAnotherRoute(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The test above is exact and has to be. This arc leaves its node and comes back a
        // different way, so its two half-edges leave in two directions and there is nothing
        // arbitrary about the order between them. What it encloses is a sliver a hundredth of a
        // unit wide, and a tolerance generous enough to call that nothing would take islands
        // and lake shores with it.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        Arc sliver = arcGraph.AddArc(
            ArcKind.Coast,
            Create(coordinateSequenceFactory, [(0.0, 0.0), (1.0, 0.0), (1.0, 0.01), (0.0, 0.0)]));

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.0);

        arcGraph.Arcs.Should().Contain(sliver);
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldKeepTheIsland_WhenAClosedArcIsShorterThanTheTolerance(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // A closed arc is not a dangle however small. Its one node carries both of its ends, so a
        // degree of two there says nothing about whether anything else meets it, and what it
        // encloses is real: this is an island two tenths of a unit across.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        Arc island = arcGraph.AddArc(
            ArcKind.Coast,
            Create(coordinateSequenceFactory, [(0.0, 0.0), (0.2, 0.0), (0.2, 0.2), (0.0, 0.0)]));

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.5);

        arcGraph.Arcs.Should().Contain(island);
    }

    [AutoCustomizedData]
    [Test]
    public void DissolveFaces_ShouldDropTheBorderBetweenThem_WhenBothFacesAreGiven(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The whole point of walking the arcs instead of unioning the geometry. The divider has
        // one of the two faces on each side, so both of its half-edges are inside the set and it
        // drops out; the four coast arcs have the set on one side only and are kept. Nothing is
        // recomputed, so the ring is made of the arcs' own coordinates.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(0, 0));
        Node east = arcGraph.AddNode(new Coordinate(10, 0));
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, 10.0), (10.0, 10.0)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, -10.0), (10.0, -10.0)]);
        Arc divider = AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Border, west, east, [(5.0, 0.0)]);
        TopologyService service = CreateService(coordinateSequenceFactory);
        service.BuildFaces(arcGraph, 0.0, 0.0);
        arcGraph.Faces.Should().HaveCount(2, "the divider cuts the ring in two");

        DirectedArc[][] rings = [.. service.DissolveFaces(arcGraph, [[.. arcGraph.Faces]])[0]];

        DirectedArc[] ring = rings.Should().ContainSingle().Subject;
        ring.Should().NotContain(directed => directed.Arc == divider);
        ring.Select(directed => directed.Arc.Kind).Should().AllBeEquivalentTo(ArcKind.Coast);
        ring.Should().HaveCount(2, "the two coast arcs are the whole of the outside");
    }

    [AutoCustomizedData]
    [Test]
    public void DissolveFaces_ShouldKeepTheBorder_WhenOnlyOneOfTheTwoFacesIsGiven(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The other side of the same rule: with one face in the set the divider has the set on one
        // side and somebody else on the other, so it is boundary and stays. That is what keeps a
        // border between two countries in both of their outlines.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(0, 0));
        Node east = arcGraph.AddNode(new Coordinate(10, 0));
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, 10.0), (10.0, 10.0)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, -10.0), (10.0, -10.0)]);
        Arc divider = AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Border, west, east, [(5.0, 0.0)]);
        TopologyService service = CreateService(coordinateSequenceFactory);
        service.BuildFaces(arcGraph, 0.0, 0.0);

        DirectedArc[][] rings = [.. service.DissolveFaces(arcGraph, [[arcGraph.Faces[0]]])[0]];

        DirectedArc[] ring = rings.Should().ContainSingle().Subject;
        ring.Should().HaveCount(2).And.Contain(directed => directed.Arc == divider);
    }

    [AutoCustomizedData]
    [Test]
    public void DissolveFaces_ShouldWindTheHoleAgainstTheGround_WhenAFaceCarriesOne(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // Two rings out of one face, and the signs are what tells the caller which is which: the
        // outside comes back with a positive signed area and the hole with a negative one, the
        // same way round 5f sorts shells from outlines.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(
            arcGraph,
            coordinateSequenceFactory,
            ArcKind.Coast,
            [(0, 0), (30, 0), (30, 30), (0, 30), (0, 0)]);
        AddArc(
            arcGraph,
            coordinateSequenceFactory,
            ArcKind.Lake,
            [(10, 10), (20, 10), (20, 20), (10, 20), (10, 10)]);
        TopologyService service = CreateService(coordinateSequenceFactory);
        service.BuildFaces(arcGraph, 0.0, 0.0);
        Face island = arcGraph.Faces.Single(face => face.Holes.Length == 1);

        DirectedArc[][] rings = [.. service.DissolveFaces(arcGraph, [[island]])[0]];

        rings.Should().HaveCount(2);
        rings.Select(GetSignedArea).Should().ContainSingle(area => area > 0.0).And.HaveCount(2);
        rings.Select(GetSignedArea).Should().ContainSingle(area => area < 0.0);
    }

    [AutoCustomizedData]
    [Test]
    public void DissolveFaces_ShouldKeepTheRim_WhenBothFacesAreGiven(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The one exception to dropping what the set has on both sides. What lies beyond the rim
        // is taken round to the other side of the map once unprojected, so the two faces it divides
        // end up 360 degrees apart, and a ring dissolved across it would jump the globe. It stays,
        // and the country comes out as a shape either side of it.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(0, 0));
        Node east = arcGraph.AddNode(new Coordinate(10, 0));
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, 10.0), (10.0, 10.0)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, -10.0), (10.0, -10.0)]);
        Arc rim = AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Rim, west, east, [(5.0, 0.0)]);
        TopologyService service = CreateService(coordinateSequenceFactory);
        service.BuildFaces(arcGraph, 0.0, 0.0);

        DirectedArc[][] rings = [.. service.DissolveFaces(arcGraph, [[.. arcGraph.Faces]])[0]];

        rings.Should().HaveCount(2);
        rings.Should().AllSatisfy(ring => ring.Should().ContainSingle(directed => directed.Arc == rim));
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldKeepOnlyTheStretchOfRimOverLand_WhenTheRimCrossesAnIsland(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The rim goes in running the whole height of the map. Over water it would join islands
        // the map does not join, so once 5a has noded it into the coast only the stretch whose
        // middle lies inside the coast is kept, and that one divides the island in two.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        AddArc(
            arcGraph,
            coordinateSequenceFactory,
            ArcKind.Coast,
            [(0, 0), (10, 0), (10, 10), (0, 10), (0, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Rim, [(5, -20), (5, 30)]);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.0);

        Arc rim = arcGraph.Arcs.Should().ContainSingle(arc => arc.Kind == ArcKind.Rim).Subject;
        rim.GetFullCoordinates().Select(coordinate => coordinate.Y).Should().BeEquivalentTo([0.0, 10.0]);
        arcGraph.Faces.Should().HaveCount(2);
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldDropTheCoast_WhenItRunsAlongTheRimByAHair(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // Antarctica's coast is the drawn rim, and the rim traced from the projection runs a
        // thousandth of a unit from it. Between two crossings the pair encloses a hairline 5f
        // cannot order, so it is one line drawn twice like any other - but the rim is kept, since
        // it is where the map is cut and the coast only followed it. Here the sliver measures
        // 0.005, inside the narrower test the rim is held to.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(0, 0));
        Node east = arcGraph.AddNode(new Coordinate(10, 0));
        Arc coast = AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(5.0, 0.02)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, 10.0), (10.0, 10.0)]);
        Arc rim = AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Rim, west, east, [(5.0, -0.02)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, -10.0), (10.0, -10.0)]);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.0);

        arcGraph.Arcs.Should().Contain(rim).And.NotContain(coast);
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldKeepTheCoast_WhenTheLandRunsPastTheRim(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // A sliver of 0.1 between two drawn lines is one line drawn twice, and against a border
        // the coast would be the only one left. Against the rim it is land drawn past the edge of
        // the map, Anian-21's islet and Mammoth Island's halves among them, and whether it is
        // taken round to the other side or left out is for 5i to decide, not for this to cut off.
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(0, 0));
        Node east = arcGraph.AddNode(new Coordinate(10, 0));
        Arc coast = AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(5.0, 0.4)]);
        AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Coast, west, east, [(0.0, -10.0), (10.0, -10.0)]);
        Arc rim = AddArc(arcGraph, coordinateSequenceFactory, ArcKind.Rim, west, east, []);

        CreateService(coordinateSequenceFactory).BuildFaces(arcGraph, 0.0, 0.0);

        arcGraph.Arcs.Should().Contain(rim).And.Contain(coast);
        arcGraph.Faces.Should().HaveCount(2);
    }

    [AutoCustomizedData]
    [Test]
    public void DissolveFaces_ShouldThrowArgumentNullException_WhenTheFacesAreNull(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        ArcGraph arcGraph = CreateGraph(coordinateSequenceFactory);

        Action action = () => CreateService(coordinateSequenceFactory).DissolveFaces(arcGraph, null!);

        action.Should().Throw<ArgumentNullException>();
    }

    [AutoCustomizedData]
    [Test]
    public void BuildFaces_ShouldThrowArgumentNullException_WhenArcGraphIsNull(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        Action action = () => CreateService(coordinateSequenceFactory).BuildFaces(null!, 0.0, 0.0);

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

    private static double GetSignedArea(IEnumerable<DirectedArc> ring)
    {
        List<Coordinate> coordinates = ring.GetCoordinates();
        double area = 0.0;
        for (int i = 0; i < coordinates.Count; ++i)
        {
            Coordinate current = coordinates[i];
            Coordinate next = coordinates[(i + 1) % coordinates.Count];
            area += (current.X * next.Y) - (next.X * current.Y);
        }

        return area / 2.0;
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
