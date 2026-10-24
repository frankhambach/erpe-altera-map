// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Tests.Services;

using System;

using Erpe.Altera.Map.Services;
using Erpe.Testing;

using FluentAssertions;

using NetTopologySuite.Geometries;

using NUnit.Framework;

[TestFixture]
[TestOf(typeof(CorrectionService))]
public class CorrectionServiceSnapTests
{
    [AutoCustomizedData]
    [Test]
    public void Snap_ShouldMoveTheLaterCoordinateOntoTheEarlier_WhenTheyAreWithinTheTolerance(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // Earlier by x, which is the order the coordinates are swept in, not the order given.
        Coordinate later = new Coordinate(10.3, 5.2);
        Coordinate earlier = new Coordinate(10.0, 5.0);

        CreateService(coordinateSequenceFactory).Snap([later, earlier], 0.5);

        later.Equals2D(new Coordinate(10.0, 5.0)).Should().BeTrue();
        earlier.Equals2D(new Coordinate(10.0, 5.0)).Should().BeTrue("the coordinate snapped onto does not move");
    }

    [AutoCustomizedData]
    [Test]
    public void Snap_ShouldLeaveBothAlone_WhenTheyAreFurtherApartThanTheTolerance(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        Coordinate first = new Coordinate(10.0, 5.0);
        Coordinate second = new Coordinate(10.6, 5.0);

        CreateService(coordinateSequenceFactory).Snap([first, second], 0.5);

        first.Equals2D(new Coordinate(10.0, 5.0)).Should().BeTrue();
        second.Equals2D(new Coordinate(10.6, 5.0)).Should().BeTrue();
    }

    [AutoCustomizedData]
    [Test]
    public void Snap_ShouldLookPastACoordinateFarAwayInY_WhenTheNextOneAlongIsWithinTheTolerance(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The sweep is by x, so a coordinate close in x but far in y lies between two that are
        // close in both, and the sweep has to carry on past it.
        Coordinate anchor = new Coordinate(10.0, 5.0);
        Coordinate between = new Coordinate(10.1, 50.0);
        Coordinate near = new Coordinate(10.2, 5.1);

        CreateService(coordinateSequenceFactory).Snap([anchor, between, near], 0.5);

        near.Equals2D(new Coordinate(10.0, 5.0)).Should().BeTrue();
        between.Equals2D(new Coordinate(10.1, 50.0)).Should().BeTrue("it is within the tolerance in x only");
    }

    [AutoCustomizedData]
    [Test]
    public void Snap_ShouldThrowArgumentNullException_WhenCoordinatesAreNull(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        Action action = () => CreateService(coordinateSequenceFactory).Snap(null!, 0.5);

        action.Should().Throw<ArgumentNullException>();
    }

    private static CorrectionService CreateService(CoordinateSequenceFactory coordinateSequenceFactory)
    {
        return new CorrectionService(new GeometryFactory(new PrecisionModel(10000.0), 0, coordinateSequenceFactory));
    }
}
