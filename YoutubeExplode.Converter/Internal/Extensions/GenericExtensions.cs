﻿using System;

namespace YoutubeExplode.Converter.Internal.Extensions
{
    internal static class GenericExtensions
    {
        public static void Pipe<T>(this T input, Action<T> handle) => handle(input);

        public static TOut Pipe<TIn, TOut>(this TIn input, Func<TIn, TOut> transform) => transform(input);

        public static T Clamp<T>(this T value, T min, T max) where T : IComparable<T> =>
            value.CompareTo(min) <= 0
                ? min
                : value.CompareTo(max) >= 0
                    ? max
                    : value;
    }
}