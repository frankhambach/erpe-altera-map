// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Tests.Models;

using System;
using System.Linq;

using Erpe.Altera.Map.Models;

using FluentAssertions;

using NetTopologySuite.Geometries;

using NUnit.Framework;

[TestFixture]
[TestOf(typeof(RingExtensions))]
public class RingExtensionsTests
{
    [Test]
    public void GetWinding_ShouldReturnCounterClockwise_WhenTheRingRunsThatWay()
    {
        // Both writers ask this which way a ring runs, and RFC 7946 wants counter-clockwise
        // shells, so this is the answer that means "leave a shell alone".
        Ring([(0, 0), (10, 0), (10, 10), (0, 10)]).GetWinding().Should().Be(1);
    }

    [Test]
    public void GetWinding_ShouldReturnClockwise_WhenTheRingRunsTheOtherWay()
    {
        Ring([(0, 0), (0, 10), (10, 10), (10, 0)]).GetWinding().Should().Be(-1);
    }

    [Test]
    public void GetWinding_ShouldReturnNeither_WhenTheRingEnclosesNothing()
    {
        // A spike out and back encloses no area, so it has no winding and turning it would change
        // no coordinate. Both writers leave such a ring exactly as it is, which is the only way
        // they can agree about it. Anian carries one of 17 points and Namaquab one as a hole.
        Ring([(0, 0), (10, 0), (20, 0), (10, 0)]).GetWinding().Should().Be(0);
    }

    [Test]
    public void GetWinding_ShouldNotDependOnWhereTheRingStarts()
    {
        Coordinate[] ring = Ring([(0, 0), (10, 0), (10, 10), (0, 10)]);

        for (int start = 0; start < ring.Length; ++start)
        {
            Coordinate[] rotated = [.. ring.Skip(start), .. ring.Take(start)];
            rotated.GetWinding().Should().Be(1, "a ring rotated to start elsewhere is the same ring");
        }
    }

    [Test]
    public void GetSignedArea_ShouldCloseTheRunItself_WhenItDoesNotComeBackToItsStart()
    {
        // The run the arcs of a ring make is left open by GetCoordinates, so the shoelace has to
        // close it. Ten by ten, and the sign says which way round.
        Ring([(0, 0), (10, 0), (10, 10), (0, 10)]).GetSignedArea().Should().Be(100.0);
    }

    [Test]
    public void GetSignedArea_ShouldThrowArgumentNullException_WhenTheCoordinatesAreNull()
    {
        Action action = () => ((Coordinate[])null!).GetSignedArea();

        action.Should().Throw<ArgumentNullException>();
    }

    private static Coordinate[] Ring((double X, double Y)[] points)
    {
        return [.. points.Select(point => new Coordinate(point.X, point.Y))];
    }
}
