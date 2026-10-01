// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Models;

// An arc as traversed by one particular face. The face on the other side traverses the
// same arc with IsReversed flipped, which is what keeps the two sides identical.
public readonly record struct DirectedArc(Arc Arc, bool IsReversed);
