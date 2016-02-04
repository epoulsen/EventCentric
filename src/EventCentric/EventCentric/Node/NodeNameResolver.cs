﻿namespace EventCentric
{
    public static class NodeNameResolver
    {
        public static string ResolveNameOf<T>() where T : class
            => $"{typeof(T).FullName}_{typeof(T).GUID}";
    }
}
