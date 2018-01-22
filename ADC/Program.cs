using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Abp;
using Abp.Application.Services.Dto;
using Abp.AutoMapper;
using Abp.Domain.Entities;
using Abp.Domain.Repositories;
using Abp.Domain.Uow;
using Abp.EntityFrameworkCore;
using Abp.EntityFrameworkCore.Configuration;
using Abp.GeneralTree;
using Abp.Modules;
using Abp.Threading;
using AutoMapper.QueryableExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using MoreLinq;
using Newtonsoft.Json;

namespace ADC
{
    public class Region : Entity<int>, IGeneralTree<Region, int>
    {
        public string Name { get; set; }

        public string DivisionCode { get; set; }

        public string FullName { get; set; }

        public string Code { get; set; }

        public int Level { get; set; }

        public Region Parent { get; set; }

        public int? ParentId { get; set; }

        public ICollection<Region> Children { get; set; }
    }

    [AutoMapFrom(typeof(Region))]
    public class RegionDto : EntityDto<int>, IGeneralTreeDto<RegionDto, int>
    {
        public string Name { get; set; }

        public string DivisionCode { get; set; }

        public string FullName { get; set; }

        public int? ParentId { get; set; }

        public ICollection<RegionDto> Children { get; set; }
    }

    [DependsOn(typeof(AbpEntityFrameworkCoreModule), typeof(AbpAutoMapperModule), typeof(GeneralTreeModule))]
    public class RegionModule : AbpModule
    {
        public override void PreInitialize()
        {
            Configuration.DefaultNameOrConnectionString =
                "Server=.\\SQLEXPRESS; Database=RegionDb; Trusted_Connection=True;";

            Configuration.Modules.AbpEfCore().AddDbContext<RegionDbContext>(options =>
            {
                options.DbContextOptions.UseSqlServer(options.ConnectionString);
            });
        }

        public override void Initialize()
        {
            IocManager.RegisterAssemblyByConvention(Assembly.GetExecutingAssembly());
        }
    }

    public class RegionDbContext : AbpDbContext
    {
        public DbSet<Region> Region { get; set; }

        public RegionDbContext(DbContextOptions<RegionDbContext> options) : base(options)
        {
        }
    }

    public class MyDbContextContextFactory : IDesignTimeDbContextFactory<RegionDbContext>
    {
        public RegionDbContext CreateDbContext(string[] args)
        {
            var options = new DbContextOptionsBuilder<RegionDbContext>()
                .UseSqlServer("Server=.\\SQLEXPRESS; Database=RegionDb; Trusted_Connection=True;")
                .Options;

            return new RegionDbContext(options);
        }
    }

    public static class DivisionCodeExtensions
    {
        public static bool IsProvince(this string str)
        {
            return str.Contains("0000");
        }

        public static bool IsCity(this string str)
        {
            return str.Contains("00") && !str.IsProvince();
        }
    }

    internal class Program
    {
        private static void Main(string[] args)
        {
            // 替换最新的中华人民共和国县以上行政区划代码网址
            const string url = "http://www.mca.gov.cn/article/sj/tjbz/a/2017/201801/201801151447.html";

            var html = AsyncHelper.RunSync(() =>
            {
                var http = new HttpClient();
                return http.GetStringAsync(url);
            });

            var result = Regex.Matches(html, @"<td class=xl\d+>([\u4e00-\u9fa5]+|\d{6})</td>");

            var regionList = new List<Region>();

            for (var i = 0; i < result.Count / 2; i++)
            {
                regionList.Add(new Region
                {
                    Name = result[i * 2 + 1].Groups[1].Value,
                    DivisionCode = result[i * 2].Groups[1].Value
                });
            }

            var root = new Region
            {
                Name = "中国"
            };

            var lastRegion = root;
            regionList.ForEach(region =>
            {
                var currentRegion = new Region
                {
                    Name = region.Name,
                    DivisionCode = region.DivisionCode
                };

                if (region.DivisionCode.IsProvince())
                {
                    if (lastRegion.Parent == null)
                    {
                        if (lastRegion.Children == null)
                        {
                            lastRegion.Children = new List<Region>();
                        }
                        lastRegion.Children.Add(currentRegion);
                        currentRegion.Parent = lastRegion;
                        lastRegion = currentRegion;
                    }
                    else
                    {
                        lastRegion = lastRegion.DivisionCode.IsCity() ? lastRegion.Parent.Parent : lastRegion.Parent;

                        currentRegion.Parent = lastRegion;
                        if (currentRegion.Parent.Children == null)
                        {
                            currentRegion.Parent.Children = new List<Region>();
                        }
                        currentRegion.Parent.Children.Add(currentRegion);

                        lastRegion = currentRegion;
                    }
                }
                else if (region.DivisionCode.IsCity())
                {
                    currentRegion.Parent = lastRegion.DivisionCode.IsCity() ? lastRegion.Parent : lastRegion;
                    if (currentRegion.Parent.Children == null)
                    {
                        currentRegion.Parent.Children = new List<Region>();
                    }
                    currentRegion.Parent.Children.Add(currentRegion);

                    lastRegion = currentRegion;
                }
                else
                {
                    currentRegion.Parent = lastRegion;
                    if (currentRegion.Parent.Children == null)
                    {
                        currentRegion.Parent.Children = new List<Region>();
                    }
                    currentRegion.Parent.Children.Add(currentRegion);
                }
            });

            using (var abpBootstrapper = AbpBootstrapper.Create<RegionModule>())
            {
                abpBootstrapper.Initialize();

                var unitOfWorkManager = abpBootstrapper.IocManager.Resolve<IUnitOfWorkManager>();
                var regionRepository = abpBootstrapper.IocManager.Resolve<IRepository<Region, int>>();

                var watch = new Stopwatch();
                watch.Start();
                using (var uow = unitOfWorkManager.Begin())
                {
                    var regionTreeManager = abpBootstrapper.IocManager.Resolve<GeneralTreeManager<Region, int>>();

                    root.Children.ForEach(x =>
                    {
                        //把中国的root去掉
                        x.Parent = null;

                        AsyncHelper.RunSync(() => regionTreeManager.BulkCreateAsync(x));
                        unitOfWorkManager.Current.SaveChanges();
                    });

                    uow.Complete();
                }
                watch.Stop();

                using (var uow = unitOfWorkManager.Begin())
                {
                    var regions = regionRepository.GetAll().ProjectTo<RegionDto>().ToList().ToTreeDto<RegionDto, int>();
                    var json = JsonConvert.SerializeObject(regions, Formatting.Indented);
                    var file = File.OpenWrite("region.json");
                    var buffer = Encoding.UTF8.GetBytes(json);
                    file.Write(buffer, 0, buffer.Length);
                }

                //Console.WriteLine(watch.ElapsedMilliseconds / 1000);
                Console.ReadLine();
            }
        }
    }
}