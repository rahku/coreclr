// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.


using System;
using System.Collections.Generic;
using System.Reflection;
using System.Diagnostics.Contracts;
using System.IO;
using System.Runtime.Versioning;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;


namespace System.Runtime.Loader
{
    public abstract class AssemblyLoadContext
    {
        private static readonly Dictionary<long, WeakReference<AssemblyLoadContext>> ContextsToUnload = new Dictionary<long, WeakReference<AssemblyLoadContext>>();
        private static long _nextId;
        private static bool _isProcessExiting;

        // Id used by contextsToUnload
        private readonly long id;

#if FEATURE_COLLECTIBLE_ALC
        // synchronization primitive to protect against usage of this instance while unloading
        private readonly object unloadLock = new object();

        // Indicates the state of this ALC (Alive or in Unloading/Unloaded state)
        private InternalState state;
#endif

        [DllImport(JitHelpers.QCall, CharSet = CharSet.Unicode)]
        [SuppressUnmanagedCodeSecurity]
        private static extern bool CanUseAppPathAssemblyLoadContextInCurrentDomain();

        [DllImport(JitHelpers.QCall, CharSet = CharSet.Unicode)]
        [SuppressUnmanagedCodeSecurity]
#if FEATURE_COLLECTIBLE_ALC
        private static extern IntPtr InitializeAssemblyLoadContext(IntPtr ptrAssemblyLoadContext, bool fRepresentsTPALoadContext, bool isCollectible);

        [DllImport(JitHelpers.QCall, CharSet = CharSet.Unicode)]
        [SuppressUnmanagedCodeSecurity]
        private static extern void PrepareForAssemblyLoadContextRelease(IntPtr ptrNativeAssemblyLoadContext, IntPtr ptrAssemblyLoadContextStrong);
#else
        private static extern IntPtr InitializeAssemblyLoadContext(IntPtr ptrAssemblyLoadContext, bool fRepresentsTPALoadContext);
#endif

        [DllImport(JitHelpers.QCall, CharSet = CharSet.Unicode)]
        [SuppressUnmanagedCodeSecurity]
        private static extern IntPtr LoadFromStream(IntPtr ptrNativeAssemblyLoadContext, IntPtr ptrAssemblyArray, int iAssemblyArrayLen, IntPtr ptrSymbols, int iSymbolArrayLen, ObjectHandleOnStack retAssembly);

        [DllImport(JitHelpers.QCall, CharSet = CharSet.Unicode)]
        [SuppressUnmanagedCodeSecurity]
        internal static extern void InternalSetProfileRoot(string directoryPath);

        [DllImport(JitHelpers.QCall, CharSet = CharSet.Unicode)]
        [SuppressUnmanagedCodeSecurity]
        internal static extern void InternalStartProfile(string profile, IntPtr ptrNativeAssemblyLoadContext);

        static AssemblyLoadContext()
        {
            // We register the cleanup of all AssemblyLoadContext that have not been finalized in the AppContext.Unloading
            AppContext.Unloading += OnAppContextUnloading;
        }

        protected AssemblyLoadContext() : this(false, false)
        {
        }

        protected AssemblyLoadContext(bool isCollectible) : this(false, isCollectible)
        {
        }

        internal AssemblyLoadContext(bool fRepresentsTPALoadContext, bool isCollectible)
        {
#if !FEATURE_COLLECTIBLE_ALC
            if (isCollectible)
            {
                throw new InvalidOperationException(SR.AssemblyLoadContext_Constructor_CollectibleNotSupported);
            }
            // Suppress the finalizer as it is not used when FEATURE_COLLECTIBLE_ALC is not defined, but we still need it declared
            GC.SuppressFinalize(this);
#endif
            // Initialize the VM side of AssemblyLoadContext if not already done.
            IsCollectible = isCollectible;

            // Add this instance to the list of alive ALC
            lock (ContextsToUnload)
            {
                if (_isProcessExiting)
                {
                    throw new InvalidOperationException(SR.AssemblyLoadContext_Constructor_CannotInstantiateWhileUnloading);
                }

                // If this is a collectible ALC, we are creating a weak handle that will be transformed to 
                // a strong handle on unloading otherwise we use a strong handle in order to call any subscriber 
                // to the Unload event when AppDomain.ProcessExit is called
#if FEATURE_COLLECTIBLE_ALC
                var thisHandle = GCHandle.Alloc(this, IsCollectible ? GCHandleType.Weak : GCHandleType.Normal);
                var thisHandlePtr = GCHandle.ToIntPtr(thisHandle);
                m_pNativeAssemblyLoadContext = InitializeAssemblyLoadContext(thisHandlePtr, fRepresentsTPALoadContext, isCollectible);
#else
                var thisHandle = GCHandle.Alloc(this, GCHandleType.Normal);
                var thisHandlePtr = GCHandle.ToIntPtr(thisHandle);
                m_pNativeAssemblyLoadContext = InitializeAssemblyLoadContext(thisHandlePtr, fRepresentsTPALoadContext);
#endif

                // Initialize event handlers to be null by default
                Resolving = null;
                Unloading = null;

                id = _nextId++;
                ContextsToUnload.Add(id, new WeakReference<AssemblyLoadContext>(this, true));
            }
        }

        ~AssemblyLoadContext()
        {
#if FEATURE_COLLECTIBLE_ALC
            // Only valid for a Collectible ALC
            // We perform an implicit Unload if no explicit Unload has been done
            if (IsCollectible)
            {
                // When in Unloading state, we are not supposed to be called on the finalizer
                // as the native side is holding a strong reference after calling Unload
                lock (unloadLock)
                {
                    Debug.Assert(state != InternalState.Unloading);
                    if (state == InternalState.Alive)
                    {
                        GC.ReRegisterForFinalize(this);
                        UnloadCollectible();
                    }
                }
            }
#endif
        }

        public bool IsCollectible { get; }

        [DllImport(JitHelpers.QCall, CharSet = CharSet.Unicode)]
        [SuppressUnmanagedCodeSecurity]
        private static extern void LoadFromPath(IntPtr ptrNativeAssemblyLoadContet, string ilPath, string niPath, ObjectHandleOnStack retAssembly);

        [DllImport(JitHelpers.QCall, CharSet = CharSet.Unicode)]
        [SuppressUnmanagedCodeSecurity]
        private static extern void GetLoadedAssembliesInternal(ObjectHandleOnStack assemblies);

        public static Assembly[] GetLoadedAssemblies()
        {
            Assembly[] assemblies = null;
            GetLoadedAssembliesInternal(JitHelpers.GetObjectHandleOnStack(ref assemblies));
            return assemblies;
        }

        // These are helpers that can be used by AssemblyLoadContext derivations.
        // They are used to load assemblies in DefaultContext.
        public Assembly LoadFromAssemblyPath(string assemblyPath)
        {
            if (assemblyPath == null)
            {
                throw new ArgumentNullException(nameof(assemblyPath));
            }

#if FEATURE_COLLECTIBLE_ALC
            lock (unloadLock)
            {
                VerifyIsAlive();
#endif
                if (PathInternal.IsPartiallyQualified(assemblyPath))
                {
                    throw new ArgumentException(SR.Argument_AbsolutePathRequired,
                        nameof(assemblyPath));
                }

                RuntimeAssembly loadedAssembly = null;
                LoadFromPath(m_pNativeAssemblyLoadContext, assemblyPath, null, JitHelpers.GetObjectHandleOnStack(ref loadedAssembly));
                return loadedAssembly;
#if FEATURE_COLLECTIBLE_ALC
            }
#endif
        }

        public Assembly LoadFromNativeImagePath(string nativeImagePath, string assemblyPath)
        {
            if (nativeImagePath == null)
            {
                throw new ArgumentNullException(nameof(nativeImagePath));
            }

#if FEATURE_COLLECTIBLE_ALC
            lock (unloadLock)
            {
                VerifyIsAlive();
#endif
                if (PathInternal.IsPartiallyQualified(nativeImagePath))
                {
                    throw new ArgumentException(SR.Argument_AbsolutePathRequired,
                        nameof(nativeImagePath));
                }

                if (assemblyPath != null && PathInternal.IsPartiallyQualified(assemblyPath))
                {
                    throw new ArgumentException(SR.Argument_AbsolutePathRequired,
                        nameof(assemblyPath));
                }

                // Basic validation has succeeded - lets try to load the NI image.
                // Ask LoadFile to load the specified assembly in the DefaultContext
                RuntimeAssembly loadedAssembly = null;
                LoadFromPath(m_pNativeAssemblyLoadContext, assemblyPath, nativeImagePath, JitHelpers.GetObjectHandleOnStack(ref loadedAssembly));
                return loadedAssembly;
#if FEATURE_COLLECTIBLE_ALC
            }
#endif
        }

        public Assembly LoadFromStream(Stream assembly)
        {
            return LoadFromStream(assembly, null);
        }

        public Assembly LoadFromStream(Stream assembly, Stream assemblySymbols)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }
#if FEATURE_COLLECTIBLE_ALC
            lock (unloadLock)
            {
                VerifyIsAlive();
#endif
                int iAssemblyStreamLength = (int) assembly.Length;
                int iSymbolLength = 0;

                // Allocate the byte[] to hold the assembly
                byte[] arrAssembly = new byte[iAssemblyStreamLength];

                // Copy the assembly to the byte array
                assembly.Read(arrAssembly, 0, iAssemblyStreamLength);

                // Get the symbol stream in byte[] if provided
                byte[] arrSymbols = null;
                if (assemblySymbols != null)
                {
                    iSymbolLength = (int) assemblySymbols.Length;
                    arrSymbols = new byte[iSymbolLength];

                    assemblySymbols.Read(arrSymbols, 0, iSymbolLength);
                }

                RuntimeAssembly loadedAssembly = null;
                unsafe
                {
                    fixed (byte* ptrAssembly = arrAssembly, ptrSymbols = arrSymbols)
                    {
                        LoadFromStream(m_pNativeAssemblyLoadContext, new IntPtr(ptrAssembly), iAssemblyStreamLength,
                            new IntPtr(ptrSymbols), iSymbolLength, JitHelpers.GetObjectHandleOnStack(ref loadedAssembly));
                    }
                }
                return loadedAssembly;
#if FEATURE_COLLECTIBLE_ALC
            }
#endif
        }

        public void Unload()
        {
            if (!IsCollectible)
            {
                throw new InvalidOperationException(SR.AssemblyLoadContext_Unload_CannotUnloadIfNotCollectible);
            }

#if FEATURE_COLLECTIBLE_ALC
            lock (unloadLock)
            {
                UnloadCollectible();
            }
#endif
        }

#if FEATURE_COLLECTIBLE_ALC
        private void UnloadCollectible()
        {
            Debug.Assert(IsCollectible);
            if (state == InternalState.Alive)
            {
                // Only if this ALC is collectible
                var thisStrongHandle = GCHandle.Alloc(this, GCHandleType.Normal);
                var thisStrongHandlePtr = GCHandle.ToIntPtr(thisStrongHandle);

                // The underlying code will transform the original weak handle 
                // created by InitializeLoadContext to a strong handle
                PrepareForAssemblyLoadContextRelease(m_pNativeAssemblyLoadContext, thisStrongHandlePtr);
            }
            else
            {
                throw new InvalidOperationException(SR.AssemblyLoadContext_Unload_AlreadyUnloaded);
            }
            state = InternalState.Unloading;
        }

        private void VerifyIsAlive()
        {
            if (state != InternalState.Alive)
            {
                throw new InvalidOperationException(SR.AssemblyLoadContext_Verify_NotUnloading);
            }
        }
#endif

        // Custom AssemblyLoadContext implementations can override this
        // method to perform custom processing and use one of the protected
        // helpers above to load the assembly.
        protected abstract Assembly Load(AssemblyName assemblyName);

        // This method is invoked by the VM when using the host-provided assembly load context
        // implementation.
        private static Assembly Resolve(IntPtr gchManagedAssemblyLoadContext, AssemblyName assemblyName)
        {
            AssemblyLoadContext context = (AssemblyLoadContext)(GCHandle.FromIntPtr(gchManagedAssemblyLoadContext).Target);

            return context.ResolveUsingLoad(assemblyName);
        }

        // This method is invoked by the VM to resolve an assembly reference using the Resolving event
        // after trying assembly resolution via Load override and TPA load context without success.
        private static Assembly ResolveUsingResolvingEvent(IntPtr gchManagedAssemblyLoadContext, AssemblyName assemblyName)
        {
            AssemblyLoadContext context = (AssemblyLoadContext)(GCHandle.FromIntPtr(gchManagedAssemblyLoadContext).Target);

            // Invoke the AssemblyResolve event callbacks if wired up
            return context.ResolveUsingEvent(assemblyName);
        }

        private Assembly GetFirstResolvedAssembly(AssemblyName assemblyName)
        {
            Assembly resolvedAssembly = null;

            Func<AssemblyLoadContext, AssemblyName, Assembly> assemblyResolveHandler = Resolving;

            if (assemblyResolveHandler != null)
            {
                // Loop through the event subscribers and return the first non-null Assembly instance
                Delegate[] arrSubscribers = assemblyResolveHandler.GetInvocationList();
                for (int i = 0; i < arrSubscribers.Length; i++)
                {
                    resolvedAssembly = ((Func<AssemblyLoadContext, AssemblyName, Assembly>)arrSubscribers[i])(this, assemblyName);
                    if (resolvedAssembly != null)
                    {
                        break;
                    }
                }
            }

            return resolvedAssembly;
        }

        private Assembly ValidateAssemblyNameWithSimpleName(Assembly assembly, string requestedSimpleName)
        {
            // Get the name of the loaded assembly
            string loadedSimpleName = null;

            // Derived type's Load implementation is expected to use one of the LoadFrom* methods to get the assembly
            // which is a RuntimeAssembly instance. However, since Assembly type can be used build any other artifact (e.g. AssemblyBuilder),
            // we need to check for RuntimeAssembly.
            RuntimeAssembly rtLoadedAssembly = assembly as RuntimeAssembly;
            if (rtLoadedAssembly != null)
            {
                loadedSimpleName = rtLoadedAssembly.GetSimpleName();
            }

            // The simple names should match at the very least
            if (String.IsNullOrEmpty(loadedSimpleName) || (!requestedSimpleName.Equals(loadedSimpleName, StringComparison.InvariantCultureIgnoreCase)))
                throw new InvalidOperationException(SR.Argument_CustomAssemblyLoadContextRequestedNameMismatch);

            return assembly;
        }

        private Assembly ResolveUsingLoad(AssemblyName assemblyName)
        {
            string simpleName = assemblyName.Name;
            Assembly assembly = Load(assemblyName);

            if (assembly != null)
            {
                assembly = ValidateAssemblyNameWithSimpleName(assembly, simpleName);
            }

            return assembly;
        }

        private Assembly ResolveUsingEvent(AssemblyName assemblyName)
        {
            string simpleName = assemblyName.Name;

            // Invoke the AssemblyResolve event callbacks if wired up
            Assembly assembly = GetFirstResolvedAssembly(assemblyName);
            if (assembly != null)
            {
                assembly = ValidateAssemblyNameWithSimpleName(assembly, simpleName);
            }

            // Since attempt to resolve the assembly via Resolving event is the last option,
            // throw an exception if we do not find any assembly.
            if (assembly == null)
            {
                throw new FileNotFoundException(SR.IO_FileLoad, simpleName);
            }

            return assembly;
        }

        /// <summary>
        /// This method is called back by the native code after Unloading has been initiated
        /// This method is called indirectly by the finalizer of the LoaderAllocator
        /// </summary>
        private void OnUnloading()
        {
#if FEATURE_COLLECTIBLE_ALC
            lock (unloadLock)
            {
                if (state == InternalState.Unloading)
                {
                    state = InternalState.Unloaded;
                }
                else
                {
                    // Otherwise we didn't have time to be called here and we will be 
                    // called via the finalizer
                    return;
                }
#endif

                var unloading = Unloading;
                // TODO: should we enclose this with a try catch?
                unloading?.Invoke(this);
#if FEATURE_COLLECTIBLE_ALC
            }
#endif
        }

#if FEATURE_COLLECTIBLE_ALC
        private static void OnUnloadingStatic(IntPtr gchManagedAssemblyLoadContext)
        {
            // This method is invoked by the VM after an Unload has been requested
            var context = (AssemblyLoadContext) GCHandle.FromIntPtr(gchManagedAssemblyLoadContext).Target;
            context.OnUnloading();
        }
#endif

        public Assembly LoadFromAssemblyName(AssemblyName assemblyName)
        {
            // Attempt to load the assembly, using the same ordering as static load, in the current load context.
            Assembly loadedAssembly = Assembly.Load(assemblyName, m_pNativeAssemblyLoadContext);

            return loadedAssembly;
        }

        [DllImport(JitHelpers.QCall, CharSet = CharSet.Unicode)]
        [SuppressUnmanagedCodeSecurity]
        private static extern IntPtr InternalLoadUnmanagedDllFromPath(string unmanagedDllPath);

        // This method provides a way for overriders of LoadUnmanagedDll() to load an unmanaged DLL from a specific path in a
        // platform-independent way. The DLL is loaded with default load flags.
        protected IntPtr LoadUnmanagedDllFromPath(string unmanagedDllPath)
        {
            if (unmanagedDllPath == null)
            {
                throw new ArgumentNullException(nameof(unmanagedDllPath));
            }
            if (unmanagedDllPath.Length == 0)
            {
                throw new ArgumentException(SR.Argument_EmptyPath, nameof(unmanagedDllPath));
            }
            if (PathInternal.IsPartiallyQualified(unmanagedDllPath))
            {
                throw new ArgumentException(SR.Argument_AbsolutePathRequired, nameof(unmanagedDllPath));
            }

            return InternalLoadUnmanagedDllFromPath(unmanagedDllPath);
        }

        // Custom AssemblyLoadContext implementations can override this
        // method to perform the load of unmanaged native dll
        // This function needs to return the HMODULE of the dll it loads
        protected virtual IntPtr LoadUnmanagedDll(String unmanagedDllName)
        {
            //defer to default coreclr policy of loading unmanaged dll
            return IntPtr.Zero;
        }

        // This method is invoked by the VM when using the host-provided assembly load context
        // implementation.
        private static IntPtr ResolveUnmanagedDll(String unmanagedDllName, IntPtr gchManagedAssemblyLoadContext)
        {
            AssemblyLoadContext context = (AssemblyLoadContext)(GCHandle.FromIntPtr(gchManagedAssemblyLoadContext).Target);
            return context.LoadUnmanagedDll(unmanagedDllName);
        }

        public static AssemblyLoadContext Default
        {
            get
            {
                if (s_DefaultAssemblyLoadContext == null)
                {
                    // Try to initialize the default assembly load context with apppath one if we are allowed to
                    if (AssemblyLoadContext.CanUseAppPathAssemblyLoadContextInCurrentDomain())
                    {
                        // Synchronize access to initializing Default ALC
                        lock (s_initLock)
                        {
                            if (s_DefaultAssemblyLoadContext == null)
                            {
                                s_DefaultAssemblyLoadContext = new AppPathAssemblyLoadContext();
                            }
                        }
                    }
                }

                return s_DefaultAssemblyLoadContext;
            }
        }

        // This call opens and closes the file, but does not add the
        // assembly to the domain.
        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        static internal extern AssemblyName nGetFileInformation(String s);

        // Helper to return AssemblyName corresponding to the path of an IL assembly
        public static AssemblyName GetAssemblyName(string assemblyPath)
        {
            if (assemblyPath == null)
            {
                throw new ArgumentNullException(nameof(assemblyPath));
            }

            string fullPath = Path.GetFullPath(assemblyPath);
            return nGetFileInformation(fullPath);
        }

        [DllImport(JitHelpers.QCall, CharSet = CharSet.Unicode)]
        [SuppressUnmanagedCodeSecurity]
        private static extern IntPtr GetLoadContextForAssembly(RuntimeAssembly assembly);

        // Returns the load context in which the specified assembly has been loaded
        public static AssemblyLoadContext GetLoadContext(Assembly assembly)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            AssemblyLoadContext loadContextForAssembly = null;

            RuntimeAssembly rtAsm = assembly as RuntimeAssembly;

            // We only support looking up load context for runtime assemblies.
            if (rtAsm != null)
            {
                IntPtr ptrAssemblyLoadContext = GetLoadContextForAssembly(rtAsm);
                if (ptrAssemblyLoadContext == IntPtr.Zero)
                {
                    // If the load context is returned null, then the assembly was bound using the TPA binder
                    // and we shall return reference to the active "Default" binder - which could be the TPA binder
                    // or an overridden CLRPrivBinderAssemblyLoadContext instance.
                    loadContextForAssembly = AssemblyLoadContext.Default;
                }
                else
                {
                    loadContextForAssembly = (AssemblyLoadContext)(GCHandle.FromIntPtr(ptrAssemblyLoadContext).Target);
                }
            }

            return loadContextForAssembly;
        }

        // Set the root directory path for profile optimization.
        public void SetProfileOptimizationRoot(string directoryPath)
        {
            InternalSetProfileRoot(directoryPath);
        }

        // Start profile optimization for the specified profile name.
        public void StartProfileOptimization(string profile)
        {
            InternalStartProfile(profile, m_pNativeAssemblyLoadContext);
        }

        private void OnAppContextUnloading()
        {
#if FEATURE_COLLECTIBLE_ALC
            lock (unloadLock)
            {
                if (state == InternalState.Alive)
                {
                    state = InternalState.Unloading;
                }
            }
#endif
            OnUnloading();
        }

        private static void OnAppContextUnloading(object sender, EventArgs e)
        {
            lock (ContextsToUnload)
            {
                _isProcessExiting = true;
                foreach (var alcAlive in ContextsToUnload)
                {
                    AssemblyLoadContext alc;
                    if (alcAlive.Value.TryGetTarget(out alc))
                    {
                        // Should we use a try/catch?
                        alc.OnAppContextUnloading();
                    }
                }
                ContextsToUnload.Clear();
            }
        }

        public event Func<AssemblyLoadContext, AssemblyName, Assembly> Resolving;
        public event Action<AssemblyLoadContext> Unloading;

        // Contains the reference to VM's representation of the AssemblyLoadContext
        private IntPtr m_pNativeAssemblyLoadContext;

        // Each AppDomain contains the reference to its AssemblyLoadContext instance, if one is
        // specified by the host. By having the field as a static, we are
        // making it an AppDomain-wide field.
        private static volatile AssemblyLoadContext s_DefaultAssemblyLoadContext;

        // Synchronization primitive for controlling initialization of Default load context
        private static readonly object s_initLock = new Object();

        // Occurs when an Assembly is loaded
        public static event AssemblyLoadEventHandler AssemblyLoad
        {
            add { AppDomain.CurrentDomain.AssemblyLoad += value; }
            remove { AppDomain.CurrentDomain.AssemblyLoad -= value; }
        }

        // Occurs when resolution of type fails
        public static event ResolveEventHandler TypeResolve
        {
            add { AppDomain.CurrentDomain.TypeResolve += value; }
            remove { AppDomain.CurrentDomain.TypeResolve -= value; }
        }

        // Occurs when resolution of resource fails
        public static event ResolveEventHandler ResourceResolve
        {
            add { AppDomain.CurrentDomain.ResourceResolve += value; }
            remove { AppDomain.CurrentDomain.ResourceResolve -= value; }
        }

        // Occurs when resolution of assembly fails
        // This event is fired after resolve events of AssemblyLoadContext fails
        public static event ResolveEventHandler AssemblyResolve
        {
            add { AppDomain.CurrentDomain.AssemblyResolve += value; }
            remove { AppDomain.CurrentDomain.AssemblyResolve -= value; }
        }
#if FEATURE_COLLECTIBLE_ALC
        private enum InternalState
        {
            /// <summary>
            /// The ALC is alive (default)
            /// </summary>
            Alive,

            /// <summary>
            /// The unload process has started, the Unloading event will be called
            /// once the underlying LoaderAllocator has been finalized
            /// </summary>
            Unloading,

            /// <summary>
            /// The event Unloading has been called.
            /// </summary>
            Unloaded
        }
#endif
    }

    internal class AppPathAssemblyLoadContext : AssemblyLoadContext
    {
#if FEATURE_COLLECTIBLE_ALC
        internal AppPathAssemblyLoadContext() : base(true, false)
#else
        internal AppPathAssemblyLoadContext() : base(true)
#endif
        {
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            // We were loading an assembly into TPA ALC that was not found on TPA list. As a result we are here.
            // Returning null will result in the AssemblyResolve event subscribers to be invoked to help resolve the assembly.
            return null;
        }
    }

    internal class IndividualAssemblyLoadContext : AssemblyLoadContext
    {
#if FEATURE_COLLECTIBLE_ALC
        internal IndividualAssemblyLoadContext() : base(false, false)
#else
        internal IndividualAssemblyLoadContext() : base(false)
#endif
        {
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            return null;
        }
    }

}

