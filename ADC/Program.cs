using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using Abp;
using Abp.Domain.Entities;
using Abp.Domain.Repositories;
using Abp.Domain.Uow;
using Abp.EntityFrameworkCore;
using Abp.GeneralTree;
using Abp.Modules;
using Abp.Threading;
using Castle.MicroKernel.Registration;
using Microsoft.EntityFrameworkCore;
using MoreEnumerable = MoreLinq.MoreEnumerable;

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

    [DependsOn(typeof(AbpEntityFrameworkCoreModule), typeof(GeneralTreeModule))]
    public class MyModule : AbpModule
    {
        public override void PreInitialize()
        {
            //UseInMemoryDatabase
            Configuration.UnitOfWork.IsTransactional = false;
            var options = new DbContextOptionsBuilder<MyDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;

            /*
            var options = new DbContextOptionsBuilder<MyDbContext>()
                .UseSqlServer("Server=.\\SQLEXPRESS; Database=RegionDb; Trusted_Connection=True;")
                .Options;
            */
            IocManager.IocContainer.Register(
                Component.For<DbContextOptions<MyDbContext>>().Instance(options));
        }

        public override void Initialize()
        {
            IocManager.RegisterAssemblyByConvention(Assembly.GetExecutingAssembly());
        }
    }

    public class MyDbContext : AbpDbContext
    {
        public DbSet<Region> Region { get; set; }

        public MyDbContext(DbContextOptions<MyDbContext> options) : base(options)
        {
        }
    }

    public static class StringExtensions
    {
        public static bool Is2Root(this string str)
        {
            return str.Contains("0000");
        }

        public static bool Is3Root(this string str)
        {
            return str.Contains("00") && !str.Is2Root();
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

            var lastRoot = root;
            regionList.ForEach(region =>
            {
                var curr = new Region
                {
                    Name = region.Name,
                    DivisionCode = region.DivisionCode
                };

                if (region.DivisionCode.Is2Root())
                {
                    if (lastRoot.Parent == null)
                    {
                        if (lastRoot.Children == null)
                        {
                            lastRoot.Children = new List<Region>();
                        }
                        lastRoot.Children.Add(curr);
                        curr.Parent = lastRoot;
                        lastRoot = curr;
                    }
                    else
                    {
                        lastRoot = lastRoot.DivisionCode.Is3Root() ? lastRoot.Parent.Parent : lastRoot.Parent;

                        curr.Parent = lastRoot;
                        if (curr.Parent.Children == null)
                        {
                            curr.Parent.Children = new List<Region>();
                        }
                        curr.Parent.Children.Add(curr);

                        lastRoot = curr;
                    }
                }
                else if (region.DivisionCode.Is3Root())
                {
                    curr.Parent = lastRoot.DivisionCode.Is3Root() ? lastRoot.Parent : lastRoot;
                    if (curr.Parent.Children == null)
                    {
                        curr.Parent.Children = new List<Region>();
                    }
                    curr.Parent.Children.Add(curr);

                    lastRoot = curr;
                }
                else
                {
                    curr.Parent = lastRoot;
                    if (curr.Parent.Children == null)
                    {
                        curr.Parent.Children = new List<Region>();
                    }
                    curr.Parent.Children.Add(curr);
                }
            });

            using (var abpBootstrapper = AbpBootstrapper.Create<MyModule>())
            {
                abpBootstrapper.Initialize();

                var unitOfWorkManager = abpBootstrapper.IocManager.Resolve<IUnitOfWorkManager>();
                var regionRepository = abpBootstrapper.IocManager.Resolve<IRepository<Region, int>>();
                using (var uow = unitOfWorkManager.Begin())
                {
                    var regionTreeManager = abpBootstrapper.IocManager.Resolve<GeneralTreeManager<Region, int>>();

                    MoreEnumerable.ForEach(root.Children, x =>
                    {
                        x.Parent = null;
                        AsyncHelper.RunSync(() => regionTreeManager.BulkCreateAsync(x));
                        unitOfWorkManager.Current.SaveChanges();
                    });

                    uow.Complete();
                }

                using (var uow = unitOfWorkManager.Begin())
                {
                    var regions = regionRepository.GetAll().ToList();
                }
            }

            Console.WriteLine("Hello World!");
        }
    }
}