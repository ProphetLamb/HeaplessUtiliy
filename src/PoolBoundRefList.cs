﻿using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using HeaplessUtility.DebuggerViews;
using HeaplessUtility.Exceptions;

namespace HeaplessUtility
{
    /// <summary>
    ///     Represents a strongly typed list of object that can be accessed by ref. Provides a similar interface as <see cref="List{T}"/>. 
    /// </summary>
    /// <typeparam name="T">The type of items of the list.</typeparam>
    [DebuggerDisplay("{GetDebuggerDisplay(),nq}")]
    [DebuggerTypeProxy(typeof(IReadOnlyCollectionDebugView<>))]
    public class PoolBoundRefList<T> :
        IReadOnlyCollection<T>,
        IDisposable
    {
        private T[]? _storage;
        private int _count;
        private bool _isPoolBound;

        public PoolBoundRefList()
        {
            _isPoolBound = true;
        }

        /// <summary>
        ///     Initializes a new list with a initial buffer.
        /// </summary>
        /// <param name="initialBuffer">The initial buffer.</param>
        public PoolBoundRefList(T[] initialBuffer)
        {
            _storage = initialBuffer;
            _count = 0;
            _isPoolBound = true;
        }
        
        /// <summary>
        ///     Initializes a new list with a specified minimum initial capacity.
        /// </summary>
        /// <param name="initialMinimumCapacity">The minimum initial capacity.</param>
        public PoolBoundRefList(int initialMinimumCapacity)
        {
            _storage = ArrayPool<T>.Shared.Rent(initialMinimumCapacity);
            _count = 0;
            _isPoolBound = true;
        }
        
        /// <inheritdoc cref="List{T}.Capacity"/>
        public int Capacity
        {
            get
            {
                ThrowHelper.ThrowIfObjectNotInitialized(_storage == null);
                return _storage.Length;
            }
        }

        /// <inheritdoc cref="List{T}.Count"/>
        public int Count
        {
            get => _count;
            set
            {
                Debug.Assert(value >= 0);
                Debug.Assert(_storage == null || value <= _storage.Length);
                _count = value;
            }
        }

        public bool IsPoolBound
        {
            get => _isPoolBound;
            set
            {
                if (_isPoolBound && !value && _storage != null)
                {
                    T[] storage = new T[Math.Max(16, (uint)_count)];
                    Array.Copy(_storage, 0, storage, 0, _count);
                    ArrayPool<T>.Shared.Return(_storage);
                    _storage = storage;
                }

                _isPoolBound = value;
            }
        }

        /// <inheritdoc cref="Span{T}.IsEmpty"/>
        public bool IsEmpty => 0 >= (uint)_count;

        /// <summary>
        ///     Returns the underlying storage of the list.
        /// </summary>
        public Span<T> RawStorage => _storage;

        /// <inheritdoc cref="List{T}.this"/>
        public ref T this[int index]
        {
            get
            {
                ThrowHelper.ThrowIfObjectNotInitialized(_storage == null);
                Debug.Assert(index < _count);
                return ref _storage[index];
            }
        }

        public ref T this[Index index]
        {
            get
            {
                ThrowHelper.ThrowIfObjectNotInitialized(_storage == null);
                return ref _storage[index];
            }
        }

        public ReadOnlySpan<T> this[Range range]
        {
            get
            {
                ThrowHelper.ThrowIfObjectNotInitialized(_storage == null);
                return _storage[range];
            }
        }

        /// <summary>
        ///     Ensures that the list has a minimum capacity. 
        /// </summary>
        /// <param name="capacity">The minimum capacity.</param>
        /// <returns>The new capacity.</returns>
        public int EnsureCapacity(int capacity)
        {
            // This is not expected to be called this with negative capacity
            Debug.Assert(capacity >= 0);

            // If the caller has a bug and calls this with negative capacity, make sure to call Grow to throw an exception.
            if (_storage == null || (uint)capacity > (uint)_storage.Length)
            {
                Grow(capacity - _count);
            }

            return Capacity;
        }

        /// <summary>
        ///     Get a pinnable reference to the list.
        ///     Does not ensure there is a null T after <see cref="Count" />
        ///     This overload is pattern matched in the C# 7.3+ compiler so you can omit
        ///     the explicit method call, and write eg "fixed (T* c = list)"
        /// </summary>
        public unsafe ref T GetPinnableReference()
        {
            ref T ret = ref Unsafe.AsRef<T>(null);
            if (_storage != null && 0 >= (uint)_storage.Length)
            {
                ret = ref _storage[0];
            }

            return ref ret;
        }

        /// <summary>
        ///     Returns a span around the contents of the list.
        /// </summary>
        public ReadOnlySpan<T> AsSpan()
        {
            return new(_storage, 0, _count);
        }
        
        /// <summary>
        ///     Returns a span around a portion of the contents of the list.
        /// </summary>
        /// <param name="start">The zero-based index of the first element.</param>
        /// <returns>The span representing the content.</returns>
        public ReadOnlySpan<T> AsSpan(int start)
        {
            return new(_storage, start, _count - start);
        }

        /// <summary>
        ///     Returns a span around a portion of the contents of the list. 
        /// </summary>
        /// <param name="start">The zero-based index of the first element.</param>
        /// <param name="length">The number of elements from the <paramref name="start"/>.</param>
        /// <returns></returns>
        public ReadOnlySpan<T> AsSpan(int start, int length)
        {
            return new(_storage, start, length);
        }

        /// <inheritdoc cref="List{T}.Add"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(in T value)
        {
            int pos = _count;

            if (_storage != null && (uint)pos < (uint)_storage.Length)
            {
                _storage[pos] = value;
                _count = pos + 1;
            }
            else
            {
                GrowAndAppend(value);
            }
        }
        
        /// <inheritdoc cref="List{T}.AddRange"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRange(ReadOnlySpan<T> value)
        {
            int pos = _count;
            if (_storage == null || pos > _storage.Length - value.Length)
            {
                Grow(value.Length);
            }
            
            value.CopyTo(_storage.AsSpan(_count));
            _count += value.Length;
        }
    
        /// <summary>
        ///     Appends a span to the list, and return the handle.
        /// </summary>
        /// <param name="length">The length of the span to add.</param>
        /// <returns>The span appended to the list.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<T> AppendSpan(int length)
        {
            int origPos = _count;
            if (_storage == null || origPos > _storage.Length - length)
            {
                Grow(length);
            }

            _count = origPos + length;
            return _storage.AsSpan(origPos, length);
        }

        /// <inheritdoc cref="List{T}.BinarySearch(T)"/>
        public int BinarySearch(in T item)
        {
            if (_storage == null)
            {
                return -1;
            }
            return Array.BinarySearch(_storage, item, Comparer<T>.Default);
        }

        /// <inheritdoc cref="List{T}.BinarySearch(T, IComparer{T})"/>
        public int BinarySearch(in T item, IComparer<T> comparer)
        {
            if (_storage == null)
            {
                return -1;
            }
            return Array.BinarySearch(_storage, item, comparer);
        }

        /// <inheritdoc cref="List{T}.BinarySearch(int, int, T, IComparer{T})"/>
        public int BinarySearch(int index, int count, in T item, IComparer<T> comparer)
        {
            if (_storage == null)
            {
                return -1;
            }
            return Array.BinarySearch(_storage, index, count, item, comparer);
        }

        /// <inheritdoc cref="List{T}.Clear"/>
        public void Clear()
        {
            if (_storage != null)
            {
                Array.Clear(_storage, 0, _count);
            }
            _count = 0;
        }

        /// <inheritdoc cref="List{T}.Contains"/>
        public bool Contains(in T item) => IndexOf(item, null) >= 0;
        
        /// <summary>
        ///     Determines whether an element is in the list.
        /// </summary>
        /// <param name="item">The object to locate in the list. The value can be null for reference types.</param>
        /// <param name="comparer">The comparer used to determine whether two items are equal.</param>
        /// <returns><see langword="true"/> if item is found in the list; otherwise, <see langword="false"/>.</returns>
        public bool Contains(in T item, IEqualityComparer<T>? comparer) => IndexOf(item, comparer) >= 0;

        /// <inheritdoc cref="Span{T}.CopyTo"/>
        public void CopyTo(Span<T> destination)
        {
            _storage.CopyTo(destination);
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            if (_storage == null)
            {
                return;
            }
            Array.Copy(_storage, 0, array, arrayIndex, _count);
        }
        
        /// <inheritdoc cref="IList{T}.IndexOf"/>
        public int IndexOf(in T item) => IndexOf(item, null);

        /// <inheritdoc cref="IList{T}.IndexOf"/>
        public int IndexOf(in T item, IEqualityComparer<T>? comparer)
        {
            if (_storage == null)
            {
                return - 1;
            }
            
            if (comparer == null)
            {
                if (typeof(T).IsValueType)
                {
                    
                    for (int i = 0; i < _count; i++)
                    {
                        if (!EqualityComparer<T>.Default.Equals(item, _storage[i]))
                        {
                            continue;
                        }

                        return i;
                    }

                    return -1;
                }
                
                comparer = EqualityComparer<T>.Default;
            }
            
            for (int i = 0; i < _count; i++)
            {
                if (!comparer.Equals(item, _storage[i]))
                {
                    continue;
                }

                return i;
            }

            return -1;
        }

        /// <inheritdoc cref="List{T}.Insert"/>
        public void Insert(int index, in T value)
        {
            if (_storage == null || _count > _storage.Length - 1)
            {
                Grow(1);
            }

            T[] storage = _storage!;
            Array.Copy(storage, index, storage, index + 1, _count - index);
            storage[index] = value;
            _count += 1;
        }
        
        /// <inheritdoc cref="List{T}.InsertRange"/>
        public void InsertRange(int index, ReadOnlySpan<T> span)
        {
            if (index < 0)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException_LessZero(ExceptionArgument.index);
            }
            if (index > _count)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException_ArrayIndexOverMax(ExceptionArgument.index, index);
            }

            int count = span.Length;
            if (count == 0)
            {
                return;
            }

            if (_storage == null || _count > _storage.Length - count)
            {
                Grow(count);
            }

            T[] storage = _storage!;
            Array.Copy(storage, index, storage, index + count, _count - index);
            span.CopyTo(storage.AsSpan(index));
            _count += count;
        }
        
        /// <inheritdoc cref="List{T}.LastIndexOf(T)"/>
        public int LastIndexOf(in T item) => LastIndexOf(item, null);

        /// <inheritdoc cref="List{T}.LastIndexOf(T)"/>
        public int LastIndexOf(in T item, IEqualityComparer<T>? comparer)
        {
            if (_storage == null)
            {
                return -1;
            }
            
            if (comparer == null)
            {
                if (typeof(T).IsValueType)
                {
                    
                    for (int i = _count - 1; i >= 0; i--)
                    {
                        if (!EqualityComparer<T>.Default.Equals(item, _storage[i]))
                        {
                            continue;
                        }

                        return i;
                    }

                    return -1;
                }
                
                comparer = EqualityComparer<T>.Default;
            }
            
            for (int i = _count - 1; i >= 0; i--)
            {
                if (!comparer.Equals(item, _storage[i]))
                {
                    continue;
                }

                return i;
            }

            return -1;
        }

        /// <inheritdoc cref="List{T}.Remove"/>
        public bool Remove(in T item) => Remove(item, null);

        /// <inheritdoc cref="List{T}.Remove"/>
        public bool Remove(in T item, IEqualityComparer<T>? comparer)
        {
            int index = IndexOf(item, comparer);
            if (index >= 0)
            {
                RemoveAt(index);
                return true;
            }

            return false;
        }

        /// <inheritdoc cref="List{T}.RemoveAt"/>
        public void RemoveAt(int index)
        {
            ThrowHelper.ThrowIfObjectNotInitialized(_storage == null);
            int remaining = _count - index - 1;
            Array.Copy(_storage, index + 1, _storage, index, remaining);
            _storage[--_count] = default!;
        }

        /// <inheritdoc cref="List{T}.RemoveRange"/>
        public void RemoveRange(int index, int count)
        {
            ThrowHelper.ThrowIfObjectNotInitialized(_storage == null);
            int end = _count - count;
            int remaining = end - index;
            Array.Copy(_storage, index + count, _storage, index, remaining);
            Array.Clear(_storage, end, count);
            _count = end;
        }

        /// <inheritdoc cref="List{T}.Reverse()"/>
        public void Reverse()
        {
            ThrowHelper.ThrowIfObjectNotInitialized(_storage == null);
            Array.Reverse(_storage, 0, _count);
        }

        /// <inheritdoc cref="List{T}.Reverse(int, int)"/>
        public void Reverse(int start, int count)
        {
            ThrowHelper.ThrowIfObjectNotInitialized(_storage == null);
            if ((uint)start >= (uint)_count)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException_ArrayIndexOverMax(ExceptionArgument.start, start);
            }
            if (count < 0)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException_LessZero(ExceptionArgument.count);
            }
            if (_count - start < count)
            {
                ThrowHelper.ThrowArgumentException_ArrayCapacityOverMax(ExceptionArgument.value);
            }
            Array.Reverse(_storage, start, count);
        }

        /// <inheritdoc cref="List{T}.Sort()"/>
        public void Sort()
        {
            _storage.AsSpan(0, _count).Sort();
        }

        /// <inheritdoc cref="List{T}.Sort(Comparison{T})"/>
        public void Sort(Comparison<T> comparison)
        {
            _storage.AsSpan(0, _count).Sort(comparison);
        }

        /// <inheritdoc cref="List{T}.Sort(IComparer{T})"/>
        public void Sort(IComparer<T> comparer)
        {
            _storage.AsSpan(0, _count).Sort(comparer);
        }

        /// <inheritdoc cref="List{T}.Sort(int, int, IComparer{T})"/>
        public void Sort(int start, int count, IComparer<T> comparer)
        {
            if ((uint)start >= (uint)_count)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException_ArrayIndexOverMax(ExceptionArgument.start, start);
            }
            if (count < 0)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException_LessZero(ExceptionArgument.count);
            }
            if (_count - start < count)
            {
                ThrowHelper.ThrowArgumentException_ArrayCapacityOverMax(ExceptionArgument.value);
            }
            _storage.AsSpan(start, count).Sort(comparer);
        }
        
        /// <inheritdoc cref="Span{T}.ToArray"/>
        public T[] ToArray()
        {
            if (_storage != null)
            {
                T[] array = new T[_count];
                Array.Copy(_storage, 0, array, 0, _count);
                return array;
            }

            return Array.Empty<T>();
        }
        
        /// <summary>
        /// Creates a <see cref="List{T}"/> from a <see cref="ValueList{T}"/>.
        /// </summary>
        /// <returns>A <see cref="List{T}"/> that contains elements form the input sequence.</returns>
        public List<T> ToList()
        {
            ThrowHelper.ThrowIfObjectNotInitialized(_storage == null);
            List<T> list = new(_count);
            for (int i = 0; i < _count; i++)
            {
                list.Add(_storage[i]);
            }

            return list;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            T[]? toReturn = _storage;
            _storage = null;
            _count = 0;
            if (toReturn != null)
            {
                ArrayPool<T>.Shared.Return(toReturn);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GrowAndAppend(T value)
        {
            Grow(1);
            Add(value);
        }

        /// <summary>
        ///     Resize the internal buffer either by doubling current buffer size or
        ///     by adding <paramref name="additionalCapacityBeyondPos" /> to
        ///     <see cref="_count" /> whichever is greater.
        /// </summary>
        /// <param name="additionalCapacityBeyondPos">
        ///     Number of chars requested beyond current position.
        /// </param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Grow(int additionalCapacityBeyondPos)
        {
            Debug.Assert(additionalCapacityBeyondPos > 0);

            if (_isPoolBound)
            {
                if (_storage != null)
                {
                    Debug.Assert(_count > _storage.Length - additionalCapacityBeyondPos, "Grow called incorrectly, no resize is needed.");

                    // Make sure to let Rent throw an exception if the caller has a bug and the desired capacity is negative
                    T[] poolArray = ArrayPool<T>.Shared.Rent((int)Math.Max((uint)(_count + additionalCapacityBeyondPos), (uint)_storage.Length * 2));
            
                    Array.Copy(_storage, 0, poolArray, 0, _count);

                    T[]? toReturn = _storage;
                    _storage = _storage = poolArray;
                    if (toReturn != null)
                    {
                        ArrayPool<T>.Shared.Return(toReturn);
                    }
                }
                else
                {
                    _storage = ArrayPool<T>.Shared.Rent(additionalCapacityBeyondPos);
                }
            }
            else
            {
                if (_storage != null)
                {
                    T[] storage = new T[(int)Math.Max((uint)(_count + additionalCapacityBeyondPos), (uint)_storage.Length * 2)];
                    Array.Copy(_storage, 0, storage, 0, _count);
                    _storage = storage;
                }
                else
                {
                    _storage = new T[Math.Max(16, (uint)additionalCapacityBeyondPos)];
                }
            }
        }

        private string GetDebuggerDisplay()
        {
            if (_storage == null || _count == 0)
                return "Count = 0";
            StringBuilder sb = new(256);
            sb.Append("Count = ").Append(_count);
            return sb.ToString();
        }

        public Enumerator GetEnumerator() => new(this);

        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>Enumerates the elements of a <see cref="PoolBoundRefList{T}"/>.</summary>
        public struct Enumerator : IEnumerator<T>
        {
            private readonly PoolBoundRefList<T> _list;
            private int _index;
            
            /// <summary>Initialize the enumerator.</summary>
            /// <param name="list">The list to enumerate.</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Enumerator(PoolBoundRefList<T> list)
            {
                _list = list;
                _index = -1;
            }

            /// <summary>Advances the enumerator to the next element of the span.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
                int index = _index + 1;
                if ((uint)index < (uint)_list.Count)
                {
                    _index = index;
                    return true;
                }

                return false;
            }

            /// <summary>Gets the element at the current position of the enumerator.</summary>
            public ref readonly T Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => ref _list[_index];
            }

            T IEnumerator<T>.Current => Current;

            object? IEnumerator.Current => Current;

            /// <summary>Resets the enumerator to the initial state.</summary>
            public void Reset()
            {
                _index = -1;
            }

            /// <summary>Disposes the enumerator.</summary>
            public void Dispose()
            {
                this = default;
            }
        }
    }
}