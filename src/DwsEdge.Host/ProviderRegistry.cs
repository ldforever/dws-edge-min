using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DwsEdge.Core.Abstractions;

namespace DwsEdge.Host
{
    /// <summary>
    /// provider 插件注册表。
    ///
    /// 宿主不引用任何厂商程序集：它扫描 runtime\providers\*.dll，
    /// 找到实现 IAcquisitionProviderFactory 的类实例化。
    /// 所以"换相机"= 往 providers 目录丢一个新的 DLL，宿主和业务层都不用改。
    /// </summary>
    internal sealed class ProviderRegistry
    {
        private readonly Dictionary<string, IAcquisitionProviderFactory> _factories =
            new Dictionary<string, IAcquisitionProviderFactory>(StringComparer.OrdinalIgnoreCase);

        private static bool _resolveHandlerInstalled;

        public IList<string> ProviderIds
        {
            get { return new List<string>(_factories.Keys); }
        }

        public void LoadFromDirectory(string providersDirectory, Action<string> log)
        {
            if (!Directory.Exists(providersDirectory))
            {
                if (log != null)
                {
                    log("providers 目录不存在：" + providersDirectory);
                }
                return;
            }

            string runtimeRoot = providersDirectory;
            DirectoryInfo parent = Directory.GetParent(providersDirectory);
            if (parent != null)
            {
                runtimeRoot = parent.FullName;
            }
            EnsureResolveHandler(runtimeRoot);

            string[] files = Directory.GetFiles(providersDirectory, "*.dll");
            for (int i = 0; i < files.Length; i++)
            {
                TryLoadAssembly(files[i], log);
            }
        }

        /// <summary>
        /// 让 provider 的依赖（DwsEdge.Core、厂商 SDK 封装等）统一从宿主根目录解析。
        ///
        /// 为什么需要：VS 编译时可能把 DwsEdge.Core.dll 一起复制进 providers\ 目录，
        /// 那样 provider 会绑定到"另一份 Core"，IAcquisitionProviderFactory 的类型身份和宿主不一致，
        /// 结果就是"插件被加载了但一个工厂都找不到"。这里强制复用已加载的同名程序集。
        /// </summary>
        private static void EnsureResolveHandler(string runtimeRoot)
        {
            if (_resolveHandlerInstalled)
            {
                return;
            }
            _resolveHandlerInstalled = true;

            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs args)
            {
                try
                {
                    AssemblyName requested = new AssemblyName(args.Name);

                    Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
                    for (int i = 0; i < loaded.Length; i++)
                    {
                        if (string.Equals(loaded[i].GetName().Name, requested.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            return loaded[i];
                        }
                    }

                    string candidate = Path.Combine(runtimeRoot, requested.Name + ".dll");
                    if (File.Exists(candidate))
                    {
                        return Assembly.LoadFrom(candidate);
                    }
                }
                catch (Exception)
                {
                }

                return null;
            };
        }

        public bool Contains(string providerId)
        {
            return providerId != null && _factories.ContainsKey(providerId);
        }

        public IAcquisitionProvider Create(string providerId, ProviderSettings settings, IEventSink sink)
        {
            IAcquisitionProviderFactory factory;
            if (providerId == null || !_factories.TryGetValue(providerId, out factory))
            {
                List<string> loaded = new List<string>(_factories.Keys);
                throw new ProviderException(
                    "找不到 provider：" + providerId + "；已加载：" + string.Join(", ", loaded.ToArray()));
            }
            return factory.Create(settings, sink);
        }

        private void TryLoadAssembly(string dllPath, Action<string> log)
        {
            try
            {
                // providers 目录里可能残留依赖副本（例如 VS 编译时复制过来的 DwsEdge.Core.dll /
                // LogisticsBaseCSharp.dll）。同名程序集已经加载过就直接跳过，避免出现第二份类型身份。
                string simpleName = Path.GetFileNameWithoutExtension(dllPath);
                Assembly[] loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < loadedAssemblies.Length; i++)
                {
                    if (string.Equals(loadedAssemblies[i].GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                // 用 Load(byte[]) 而不是 LoadFrom：
                // LoadFrom 会优先在 provider 所在目录找依赖，一旦 providers\ 下残留了
                // DwsEdge.Core.dll / LogisticsBaseCSharp.dll 的副本，provider 就会绑定到
                // "另一份 Core"，导致找不到工厂。从字节加载时依赖统一走宿主根目录，
                // 保证和宿主用同一份程序集。
                Assembly assembly;
                try
                {
                    assembly = Assembly.Load(File.ReadAllBytes(dllPath));
                }
                catch (Exception)
                {
                    assembly = Assembly.LoadFrom(dllPath);
                }

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                    if (log != null)
                    {
                        Exception[] loaderExceptions = ex.LoaderExceptions;
                        for (int i = 0; i < loaderExceptions.Length; i++)
                        {
                            if (loaderExceptions[i] != null)
                            {
                                log("  类型加载失败（" + Path.GetFileName(dllPath) + "）：" + loaderExceptions[i].Message);
                            }
                        }
                    }
                }

                for (int i = 0; i < types.Length; i++)
                {
                    Type t = types[i];
                    if (t == null || t.IsAbstract || t.IsInterface)
                    {
                        continue;
                    }
                    if (!typeof(IAcquisitionProviderFactory).IsAssignableFrom(t))
                    {
                        continue;
                    }

                    IAcquisitionProviderFactory factory = (IAcquisitionProviderFactory)Activator.CreateInstance(t);
                    _factories[factory.ProviderId] = factory;
                    if (log != null)
                    {
                        log(string.Format("已加载 provider 插件：{0}（{1}）", factory.ProviderId, Path.GetFileName(dllPath)));
                    }
                }
            }
            catch (Exception ex)
            {
                if (log != null)
                {
                    log(string.Format("跳过 {0}：{1}", Path.GetFileName(dllPath), ex.Message));
                }
            }
        }
    }
}
