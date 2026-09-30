// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Tests.Testing;

using FluentAssertions;
using FluentAssertions.Execution;
using FluentAssertions.Primitives;

using NetTopologySuite.Geometries;

internal sealed class GeometryAssertions : ReferenceTypeAssertions<Geometry, GeometryAssertions>
{
    public GeometryAssertions(Geometry subject)
        : base(subject)
    {
    }

    protected override string Identifier => "geometry";

    public AndConstraint<GeometryAssertions> BeTopologicallyEqualTo(
        Geometry expected,
        string because = "",
        params object[] becauseArgs)
    {
        Execute.Assertion.ForCondition(this.Subject.EqualsTopologically(expected))
            .BecauseOf(because, becauseArgs)
            .FailWith(
                "Expected {context:object} to be topologically equal to {0}{reason}, but found {1}.",
                expected,
                this.Subject);

        return new AndConstraint<GeometryAssertions>(this);
    }
}