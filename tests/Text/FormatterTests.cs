﻿using System;
using System.Collections.Generic;

using FluentAssertions;

using NUnit.Framework;

namespace Rustic.Text.Tests;

[TestFixture]
public class FormatterTests
{
    public static IEnumerable<object[]> IndexFormatTestCases = new List<object[]>
    {
        new object[] { "Hello World", Array.Empty<string>(), "Hello World" },
        new object[] { "{0}", new[] { "Hello World" }, "Hello World" },
        new object[] { "Hello {0}", new[] { "World" }, "Hello World" },
        new object[] { "Hello{0}World", new[] { " " }, "Hello World" },
        new object[] { "{0} {1}", new[] { "Hello", "World" }, "Hello World" },
        new object[] { "{0}{1}{2}", new[] { "Hello", " ", "World" }, "Hello World" },
        new object[] { "{1}{0}{2}", new[] { " ", "Hello", "World" }, "Hello World" },
        new object[] { "", Array.Empty<string>(), "" },
        new object[] { "", new[] { "Empty", "Format" }, "" },
    };

    [Test, TestCaseSource(nameof(IndexFormatTestCases))]
    public void IndexNoValue(string format, string[] substitutes, string expected) {
        Fmt.Def.Index(format.AsSpan(), substitutes).Should().Be(expected);
    }
}
