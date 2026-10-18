// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;

using Erpe.Altera.Map.Services;

using FluentAssertions;

using NetTopologySuite.Geometries;

using NUnit.Framework;

[TestFixture]
[TestOf(typeof(VisvalingamSimplificationService))]
public class VisvalingamSimplificationServiceTests
{
    [Test]
    public void SelectRemoved_ShouldRemoveTheVerticesOnTheLine_WhenTheyAddNothing()
    {
        // Three points strung along a straight line between the ends: each makes a triangle
        // of no area at all, so none of them is carrying anything.
        Coordinate[] line =
        [
            new Coordinate(0, 0),
            new Coordinate(1, 0),
            new Coordinate(2, 0),
            new Coordinate(3, 0),
            new Coordinate(4, 0),
        ];

        Coordinate[] removed = [.. new VisvalingamSimplificationService().SelectRemoved(line, 0.5, 2)];

        removed.Should().HaveCount(3);
        removed.Should().NotContain(line[0]).And.NotContain(line[^1]);
    }

    [Test]
    public void SelectRemoved_ShouldKeepTheSharedVertex_WhenOneOfTheLinesCarryingItWillNotGiveItUp()
    {
        // The rule the network overload exists for. The long line runs dead straight, so every
        // vertex on it is worth nothing and it offers all of them up. The short line touches it
        // at one of those vertices and is held at its floor, so it offers nothing at all - and
        // it is the one that cannot afford to lose the point, because three points are all it
        // has.
        //
        // This is arc 1477 on the sample map, a five-point island, against arc 9922, 7,777
        // points of mainland coast that touches it. Pooling the two answers lost the island.
        Coordinate shared = new Coordinate(2, 0);
        (Coordinate[] Line, int MinimumCount)[] lines =
        [
            ([new Coordinate(0, 0), new Coordinate(1, 0), shared, new Coordinate(3, 0)], 2),
            ([shared, new Coordinate(2, 1), new Coordinate(3, 1), shared], 6),
        ];

        IReadOnlyCollection<(double X, double Y)> removed =
            new VisvalingamSimplificationService().SelectRemoved(lines, 0.5);

        removed.Should().NotContain((shared.X, shared.Y), "the island will not give it up");
        removed.Should().Contain((1.0, 0.0), "no other line carries that one");
    }

    [Test]
    public void SelectRemoved_ShouldRemoveTheSharedVertex_WhenEveryLineCarryingItOffersIt()
    {
        // The other half of the rule, and the half that makes borders match: two countries
        // either side of one line hold the same coordinates, both offer the same ones up, and
        // both lose them. Keeping a coordinate for one side and not the other is the single
        // thing this whole scheme exists to prevent.
        Coordinate shared = new Coordinate(2, 0);
        (Coordinate[] Line, int MinimumCount)[] lines =
        [
            ([new Coordinate(0, 0), new Coordinate(1, 0), shared, new Coordinate(4, 0)], 2),
            ([new Coordinate(0, 0), shared, new Coordinate(3, 0), new Coordinate(4, 0)], 2),
        ];

        new VisvalingamSimplificationService().SelectRemoved(lines, 0.5)
            .Should()
            .Contain((shared.X, shared.Y));
    }

    [Test]
    public void SelectRemoved_ShouldKeepAnEnd_WhenAnotherLineOffersItFromItsOwnInterior()
    {
        // Ends used to be put back by a pass of their own. They no longer need one: a line
        // never offers up its own ends, so a node is always declined by somebody however
        // freely another line gives away the same point from the middle of itself.
        Coordinate node = new Coordinate(2, 0);
        (Coordinate[] Line, int MinimumCount)[] lines =
        [
            ([new Coordinate(0, 0), new Coordinate(1, 0), node, new Coordinate(3, 0)], 2),
            ([node, new Coordinate(2, 5), new Coordinate(2, 9)], 2),
        ];

        new VisvalingamSimplificationService().SelectRemoved(lines, 0.5)
            .Should()
            .NotContain((node.X, node.Y));
    }

    [Test]
    public void SelectRemoved_ShouldRaiseNothing_WhenTheStartingCountsAlreadyHoldTheRing()
    {
        // The case that holds on the sample map at 0.001. The rings say they are happy, so the
        // guarantee confirms it and stops, and what comes out is what the counts alone would
        // have given. A guarantee that cost something every run would be the wrong guarantee.
        (Coordinate[] Line, int MinimumCount)[] lines = [(Apex(0, 0, 1, 0.1, 2, 0), 2), (Apex(2, 0, 1, -0.1, 0, 0), 2)];

        (IReadOnlySet<(double X, double Y)> removed, int raised, int rounds) =
            new VisvalingamSimplificationService().SelectRemoved(lines, [[0, 1]], (_, _) => true, 0.5);

        raised.Should().Be(0);
        rounds.Should().Be(0);
        removed.Should().BeEquivalentTo([(1.0, 0.1), (1.0, -0.1)], "both apexes carry nothing at this tolerance");
    }

    [Test]
    public void SelectRemoved_ShouldRaiseEveryLineOfTheRing_WhenTheRingWouldNotHoldTogether()
    {
        // Gibraltar: a ring of two lines, each of which simplifies to the straight run between
        // its own two ends, leaving the ring a line. Neither line can see that on its own, which
        // is the whole reason the ring is asked.
        //
        // The ring here says it holds together only while its first apex survives. Both of its
        // lines are raised, not just the one that carries the apex, because nothing tells the
        // loop which of them the ring wanted.
        (Coordinate[] Line, int MinimumCount)[] lines = [(Apex(0, 0, 1, 0.1, 2, 0), 2), (Apex(2, 0, 1, -0.1, 0, 0), 2)];

        (IReadOnlySet<(double X, double Y)> removed, int raised, int rounds) =
            new VisvalingamSimplificationService().SelectRemoved(
                lines,
                [[0, 1]],
                (_, dropped) => !dropped.Contains((1.0, 0.1)),
                0.5);

        rounds.Should().Be(1);
        raised.Should().Be(2, "both lines of the ring are raised");
        removed.Should().BeEmpty("at their own length the lines give up nothing");
    }

    [Test]
    public void SelectRemoved_ShouldStop_WhenNoCountCanSaveTheRing()
    {
        // A ring that will never say yes. The counts climb to the lines' own lengths, at which
        // nothing is taken from them at all, and then the lines drop out of the asking and this
        // ends rather than climbing for ever. Anian's islet beyond the rim is one of these and
        // is kept out of the asking from the start, but the loop has to be safe without that.
        (Coordinate[] Line, int MinimumCount)[] lines = [(Apex(0, 0, 1, 0.1, 2, 0), 2), (Apex(2, 0, 1, -0.1, 0, 0), 2)];

        (IReadOnlySet<(double X, double Y)> removed, int raised, int rounds) =
            new VisvalingamSimplificationService().SelectRemoved(lines, [[0, 1]], (_, _) => false, 0.5);

        rounds.Should().Be(1);
        raised.Should().Be(2);
        removed.Should().BeEmpty();
    }

    [Test]
    public void SelectRemoved_ShouldDoubleTheCount_WhenOneRoundIsNotEnough()
    {
        // Doubling rather than stepping, which is what keeps this to a few rounds however long
        // the line is. Nine points on one line, and a ring that holds together only once all but
        // one of them survives: the count goes 2, 4, 8, which is two raisings where stepping it
        // by one would have taken six.
        Coordinate[] line = [.. Enumerable.Range(0, 9).Select(step => new Coordinate(step, 0))];
        (Coordinate[] Line, int MinimumCount)[] lines = [(line, 2)];

        (IReadOnlySet<(double X, double Y)> removed, int _, int rounds) =
            new VisvalingamSimplificationService().SelectRemoved(
                lines,
                [[0]],
                (_, dropped) => dropped.Count <= 1,
                0.5);

        rounds.Should().Be(2);
        removed.Should().HaveCount(1);
    }

    [Test]
    public void SelectRemoved_ShouldThrowArgumentNullException_WhenTheRingPredicateIsNull()
    {
        Action action = () => new VisvalingamSimplificationService().SelectRemoved([], [], null!, 0.5);

        action.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void SelectRemoved_ShouldThrowArgumentNullException_WhenTheLinesAreNull()
    {
        Action action = () => new VisvalingamSimplificationService().SelectRemoved(null!, 0.5);

        action.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void SelectRemoved_ShouldKeepBothEnds_WhenEverythingElseGoes()
    {
        // The ends of an arc are its nodes, where it meets the arcs around it. A node that
        // vanished would pull the network apart, so no tolerance may take one.
        Coordinate[] line =
        [
            new Coordinate(0, 0),
            new Coordinate(1, 0),
            new Coordinate(2, 0),
            new Coordinate(3, 0),
        ];

        Coordinate[] removed = [.. new VisvalingamSimplificationService().SelectRemoved(line, 1000.0, 2)];

        removed.Should().HaveCount(2, "everything between the two ends");
    }

    [Test]
    public void SelectRemoved_ShouldKeepTheVertex_WhenItCarriesMoreThanTheTolerance()
    {
        // The middle vertex stands 1 unit off the line between its neighbours, which are
        // 2 apart, so its triangle is 1 square unit.
        Coordinate[] line = [new Coordinate(0, 0), new Coordinate(1, 1), new Coordinate(2, 0)];
        VisvalingamSimplificationService service = new VisvalingamSimplificationService();

        service.SelectRemoved(line, 0.9, 2).Should().BeEmpty();
        service.SelectRemoved(line, 1.1, 2).Should().HaveCount(1);
    }

    [Test]
    public void SelectRemoved_ShouldStopAtTheMinimum_WhenTheLineClosesOnItself()
    {
        // An island is one arc that closes on itself, and simplifying one down to a
        // triangle loses it altogether.
        Coordinate[] line =
        [
            new Coordinate(0, 0),
            new Coordinate(1, 0),
            new Coordinate(2, 0),
            new Coordinate(2, 1),
            new Coordinate(2, 2),
            new Coordinate(1, 2),
            new Coordinate(0, 2),
            new Coordinate(0, 1),
            new Coordinate(0, 0),
        ];

        Coordinate[] removed = [.. new VisvalingamSimplificationService().SelectRemoved(line, 1000.0, 6)];

        (line.Length - removed.Length).Should().Be(6, "the floor holds six of the nine points");
    }

    [Test]
    public void SelectRemoved_ShouldDecideTheSameWay_WhenTheLineIsWalkedBackwards()
    {
        // The property the whole of 7f rests on. Two countries either side of a border
        // walk it in opposite directions, so a decision that depended on the direction
        // would give them different geometry.
        Coordinate[] line =
        [
            new Coordinate(0, 0),
            new Coordinate(1, 0.4),
            new Coordinate(2, 0.1),
            new Coordinate(3, 0.9),
            new Coordinate(4, 0.2),
            new Coordinate(5, 0.5),
            new Coordinate(6, 0),
        ];
        Coordinate[] reversed = [.. line.Reverse()];
        VisvalingamSimplificationService service = new VisvalingamSimplificationService();

        // 0.45 takes some of them and leaves others: the effective areas along this line
        // run 0.35, 0.55, 0.75, 0.5 and 0.4 before anything is dropped.
        Coordinate[] forwards = [.. service.SelectRemoved(line, 0.45, 2)];
        Coordinate[] backwards = [.. service.SelectRemoved(reversed, 0.45, 2)];

        forwards.Should().NotBeEmpty();
        backwards.Should().BeEquivalentTo(forwards);
    }

    [Test]
    public void SelectRemoved_ShouldThrowArgumentOutOfRangeException_WhenTheToleranceIsNotPositive()
    {
        // Eager, both of the guards, so the call itself throws rather than the enumeration.
        Action action = () => new VisvalingamSimplificationService()
            .SelectRemoved([new Coordinate(0, 0), new Coordinate(1, 0)], 0.0, 2);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void SelectRemoved_ShouldThrowArgumentNullException_WhenTheLineIsNull()
    {
        Action action = () => new VisvalingamSimplificationService().SelectRemoved(null!, 1.0, 2);

        action.Should().Throw<ArgumentNullException>();
    }

    // Two ends and one apex between them, which is the smallest line with anything to lose.
    private static Coordinate[] Apex(
        double startX,
        double startY,
        double apexX,
        double apexY,
        double endX,
        double endY)
    {
        return [new Coordinate(startX, startY), new Coordinate(apexX, apexY), new Coordinate(endX, endY)];
    }
}
