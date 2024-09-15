using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using BBBirder.UnityInjection;
using UnityEngine;
using UnityEngine.Assertions;

namespace BBBirder.UnityVue
{
    internal struct LutKey : IEquatable<LutKey>
    {
        public IWatchable watched;
        public object key;
        public override int GetHashCode()
        {
            return watched.GetHashCode();
        }
        public override bool Equals(object obj)
        {
            if (obj is not LutKey lutKey || obj is null)
            {
                return false;
            }
            return object.ReferenceEquals(watched, lutKey.watched)
                && object.Equals(key, lutKey.key);
        }
        public override string ToString()
        {
            return $"{watched} -> {key}";
        }

        public bool Equals(LutKey lutKey)
        {
            return object.ReferenceEquals(watched, lutKey.watched)
                && object.Equals(key, lutKey.key);
        }
    }

    public partial class CSReactive
    {

        public struct DataAccess
        {
            public IWatchable watchable;
            public object propertyKey;
        }

        // #if UNITY_2023_3_OR_NEWER
        // See: https://issuetracker.unity3d.com/issues/crashes-on-garbagecollector-collectincremental-when-entering-the-play-mode
        // Implement(IL2CPP): https://unity.com/releases/editor/whats-new/2021.2.1
        // Fixed(IL2CPP): https://unity.com/releases/editor/beta/2023.1.0b11
        // internal static ConditionalWeakTable<IWatched,Dictionary<string,HashSet<WatchScope>>> dataDeps;
        // #endif
        private static int s_frameIndex;
        [ThreadStatic] private static bool shouldCollectReference;
        [ThreadStatic] private static WatchScope executingScope;
        internal static ConcurrentDictionary<WatchScope, bool> dirtyScopes = new();

        static void OnGlobalGet(IWatchable watched, object key)
        {
            if (!CSReactive.shouldCollectReference) return;

            if (watched.IsPropertyWatchable(key))
            {
                var propertyValue = watched.RawGet(key) as IWatchable;
                if (propertyValue != null) SetProxy(propertyValue);
            }

            if (executingScope == null) return;

            var topScope = executingScope;
            watched.Scopes ??= new();
            if (!watched.Scopes.TryGetValue(key, out var collection))
            {
                watched.Scopes[key] = collection = new();
            }
            collection.Add(topScope);
            topScope.includedTables.Add(collection);
        }


        static void OnGlobalSet(IWatchable watched, object key)
        {
            var pkey = new LutKey()
            {
                watched = watched,
                key = key,
            };
            // if (dataRegistry.TryGetValue(pkey, out var collection))
            if (watched.Scopes != null && watched.Scopes.TryGetValue(key, out var collection))
            {
                var count = collection.Count;
                var temp = ArrayPool<WatchScope>.Shared.Rent(count);
                collection.CopyTo(temp);

                for (int i = 0; i < count; i++)
                {
                    var scp = temp[i];
                    if (scp.flushMode == ScopeFlushMode.Immediate)
                    {
                        RunScope(scp);
                    }
                    else if (scp.flushMode == ScopeFlushMode.LateUpdate)
                    {
                        SetDirty(scp);
                    }
                }
                ArrayPool<WatchScope>.Shared.Return(temp, true);
            }
        }

        private static T SetProxy<T>(T watched) where T : IWatchable
        {
            if ((watched.StatusFlags & (byte)PreservedWatchableFlags.Reactive) == 0)
            {
                watched.onPropertyGet += OnGlobalGet;
                watched.onPropertySet += OnGlobalSet;
                watched.StatusFlags |= (byte)PreservedWatchableFlags.Reactive;
            }
            return watched;
        }

        #region Scope Management

        internal static void RunScope(WatchScope scope, bool invokeNormalEffect = true)
        {
            scope.isDirty = false;
            if (scope.lifeKeeper != null && !scope.lifeKeeper.IsAlive)
            {
                ClearScopeDependencies(scope);
                return;
            }

            if (scope.frameIndex != s_frameIndex)
            {
                scope.updatedInOneFrame = 0;
                scope.frameIndex = s_frameIndex;
            }

            if (scope.updateLimit != -1
                && ++scope.updatedInOneFrame > scope.updateLimit)
            {
                scope.isDirty = true;

                var frame = scope.stackFrames
                    .Where(f => f.GetMethod().GetCustomAttribute<DebuggerHiddenAttribute>() == null)
                    .FirstOrDefault();
                Logger.Warning("effect times exceed max iter count " + frame?.GetFileName() + frame?.GetFileLineNumber());
                return;
            }

            using (ActiveScopeRegion.Create(scope))
            {
                ClearScopeDependencies(scope);
                using (EnableReferenceCollectRegion.Create())
                {
                    try
                    {
                        Assert.IsNotNull(scope.effect);
                        scope.effect();
                    }
                    catch (Exception e)
                    {
                        Logger.Error(e);
                    }
                }

                try
                {
                    if (invokeNormalEffect) scope.normalEffect?.Invoke();
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                }
            }
        }

        public static void UpdateDirtyScopes()
        {
            s_frameIndex++;

            while (dirtyScopes.Count > 0)
            {
                WatchScope dirtyScope = null;
                foreach (var (scp, _) in dirtyScopes)
                {
                    dirtyScope = scp;
                    break;
                }

                RunScope(dirtyScope);
                if (IsScopeClean(dirtyScope) || IsScopeUpdatedTooMuchTimes(dirtyScope))
                {
                    dirtyScopes.Remove(dirtyScope, out _);
                }
            }
        }

        internal static void SetDirty(WatchScope scope, bool dirty = true)
        {
            scope.isDirty = dirty;
            if (dirty)
            {
                CSReactive.dirtyScopes.TryAdd(scope, true);
            }
            else
            {
                CSReactive.dirtyScopes.Remove(scope, out _);
            }
        }

        static bool IsScopeUpdatedTooMuchTimes(WatchScope scope)
        {
            return scope.updateLimit != -1
                && scope.updatedInOneFrame > scope.updateLimit
                && scope.frameIndex == s_frameIndex
                ;
        }

        static bool IsScopeClean(WatchScope scope)
        {
            return !scope.isDirty;
        }

        static void ClearScopeDependencies(WatchScope scope)
        {

            foreach (var collection in scope.includedTables)
            {
                collection.Remove(scope);
            }
            scope.includedTables.Clear();
        }

        internal static void FreeScope(WatchScope scope)
        {
            ClearScopeDependencies(scope);
            if (scope.lifeKeeper != null)
            {
                scope.lifeKeeper.onDestroyed -= scope.Dispose;
            }
            CSReactive.dirtyScopes.Remove(scope, out _);

            scope.effect = null;
            scope.normalEffect = null;
            scope.onDisposed?.Invoke();
            scope.onDisposed = null;
        }

        #endregion // Scope Management

        private struct ActiveScopeRegion : IDisposable
        {
            private WatchScope prev;
            private WatchScope current;
            public static ActiveScopeRegion Create(WatchScope scope)
            {
                var region = new ActiveScopeRegion()
                {
                    prev = executingScope,
                    current = scope,
                };
                executingScope = scope;
                return region;
            }
            public void Dispose()
            {
                if (current != executingScope)
                {
                    throw new("stacking scopes not poped in a correct order, are you access this via multi-thread?");
                }
                executingScope = prev;
            }
        }

        private struct EnableReferenceCollectRegion : IDisposable
        {
            public static EnableReferenceCollectRegion Create()
            {
                CSReactive.shouldCollectReference = true;
                return new();
            }
            public void Dispose()
            {
                CSReactive.shouldCollectReference = false;
            }
        }


    }
}
