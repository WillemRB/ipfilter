namespace IPFilter.Services
{
    using System;
    using System.Deployment.Application;
    using System.Diagnostics;
    using System.IO;
    using System.Threading.Tasks;
    using Models;

    class CacheProvider : ICacheProvider
    {
        static readonly string filterPath;

        static CacheProvider()
        {
            //string dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DavidMoore", "IPFilter");
            string dataPath = "IPFilter";

            if (ApplicationDeployment.IsNetworkDeployed)
            {
                dataPath = ApplicationDeployment.CurrentDeployment.DataDirectory;
            }

            filterPath = Path.Combine(dataPath, "ipfilter.dat");
        }
        
        public static string FilterPath
        {
            get { return filterPath; }
        }

        public async Task<FilterDownloadResult> GetAsync(FilterDownloadResult filter)
        {
            var file = new FileInfo(filterPath);

            if (!file.Exists) return null;

            var result = new FilterDownloadResult();

            result.FilterTimestamp = file.LastWriteTimeUtc;

            result.Stream = new MemoryStream((int) file.Length);

            using (var stream = file.OpenRead())
            {
                await stream.CopyToAsync(result.Stream);
            }

            result.Length = result.Stream.Length;

            return result;
        }

        public async Task SetAsync(FilterDownloadResult filter)
        {
            try
            {
                if (filter == null || filter.Exception != null) return;

                var file = new FileInfo(filterPath);

                if (file.Directory != null && !file.Directory.Exists)
                {
                    file.Directory.Create();
                }

                Trace.TraceInformation("Writing cached ipfilter to " + filterPath);
                filter.Stream.Seek(0, SeekOrigin.Begin);
                using (var cacheFile = File.Open(filterPath, FileMode.Create, FileAccess.Write,FileShare.Read))
                {
                    await filter.Stream.CopyToAsync(cacheFile);
                }

                if (filter.FilterTimestamp != null)
                {
                    file.LastWriteTimeUtc = filter.FilterTimestamp.Value.UtcDateTime;
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Couldn't write the cached ipfilter: " + ex.Message);
            }
        }
    }
}