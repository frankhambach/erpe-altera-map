// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Tests;

using AutoFixture;

using Erpe.Testing;

using NetTopologySuite;
using NetTopologySuite.Geometries;

[AutoDataCustomization]
public class CoordinateSequenceFactoryCustomization : ICustomization
{
    public void Customize(IFixture fixture)
    {
        fixture.Register(() => NtsGeometryServices.Instance.DefaultCoordinateSequenceFactory);
        fixture.Customize<Coordinate>(
            customization => customization.Without(coordinate => coordinate.Z)
                .Without(coordinate => coordinate.M)
                .Without(coordinate => coordinate.CoordinateValue));
        fixture.Customize<Envelope>(
            composer => composer.FromFactory(
                (double x1, double x2, double y1, double y2) => new Envelope(x1, x2, y1, y2)));
    }
}