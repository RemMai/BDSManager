using BDSManager.WebUI.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using BDSManager.WebUI.IO;
using System.IO.Compression;

namespace BDSManager.WebUI.Pages;
public class ManageServerModel : PageModel
{
    [BindProperty]
    public ServerModel Server { get; set; } = new();
    [BindProperty]
    public string HeaderString { get; set; } = "New Server";
    [BindProperty]
    public bool CreateNew { get; set; } = false;
    [BindProperty]
    public List<AddonPackModel> AvailableAddons { get; set; } = new();
    [BindProperty]
    public bool ShowAddons { get; set; } = false;
    [BindProperty]
    public IFormFile UploadedFile { get; set; } = null!;
    private readonly ILogger<ManageServerModel> _logger;
    private readonly IConfiguration _configuration;
    private readonly BDSUpdater _bdsUpdater;
    private readonly OptionsIO _optionsIO;
    private readonly DirectoryIO _directoryIO;
    private readonly ServerProperties _serverProperties;
    private readonly BDSAddon _bdsAddon;
    private readonly string? _serversPath;
    private readonly string? _downloadPath;
    private readonly IWebHostEnvironment _environment;

    public ManageServerModel(
        ILogger<ManageServerModel> logger, 
        IConfiguration configuration, 
        BDSUpdater bdsUpdater, 
        OptionsIO optionsIO, 
        ServerProperties serverProperties,
        BDSAddon bdsAddon,
        IWebHostEnvironment environment,
        DirectoryIO directoryIO
        )
    {
        _logger = logger;
        _configuration = configuration;
        _bdsUpdater = bdsUpdater;
        _optionsIO = optionsIO;
        _directoryIO = directoryIO;
        _serverProperties = serverProperties;
        _bdsAddon = bdsAddon;
        _environment = environment;
        _serversPath = _configuration["ServersPath"];
        _downloadPath = _configuration["DownloadPath"];
        AvailableAddons = _bdsAddon.GetAvailableAddons();
    }

    public Task<IActionResult> OnGet(bool createNew = false, string? path = null, bool newAddon = false)
    {
        HeaderString = createNew ? "New Server" : "Configure Server";
        CreateNew = createNew;
        ShowAddons = newAddon;
        if (!createNew && !string.IsNullOrEmpty(path))
        {
            if(!_optionsIO.ManagerOptions.Servers.Any(x => x.Path == path))
                return Task.FromResult<IActionResult>(RedirectToPage("./Index"));
            Server = _optionsIO.ManagerOptions.Servers.FirstOrDefault(x => x.Path == path) ?? new();
            
            return Task.FromResult<IActionResult>(Page());
        }
        else
        {
            var maxPort = _optionsIO.ManagerOptions.Servers.Max(x => x.Options.Port);
            var port = maxPort != null ? int.Parse(maxPort) + 2 : 19132;
            var portv6 = maxPort != null ? int.Parse(maxPort) + 3 : 19133;
            Server.Options.Port = port.ToString();
            Server.Options.Portv6 = portv6.ToString();
        }
        return Task.FromResult<IActionResult>(Page());

    }
    public async Task<IActionResult> OnPostNewServer()
    {
        ServerModel server = new()
        {
            Options = Server.Options
        };

        if (string.IsNullOrEmpty(server.Path))
            server.Path = GetNextServerPath();

        await _bdsUpdater.UpdateBedrockServerAsync(server);
        _optionsIO.AddServer(server);
        return RedirectToPage("./Index");
    }

    public Task<IActionResult> OnPostSaveServer(string[] ApplyAddons)
    {
        if (string.IsNullOrEmpty(Server.Path))
            return Task.FromResult<IActionResult>(RedirectToPage("./Index"));

        var server = _optionsIO.ManagerOptions.Servers.FirstOrDefault(x => x.Path == Server.Path);
        if (server == null)
            return Task.FromResult<IActionResult>(RedirectToPage("./Index"));

        Server.AllowList.RemoveAll(x => string.IsNullOrEmpty(x.name));
        Server.AllowList.ForEach(x =>
        {
            if(string.IsNullOrEmpty(x.xuid))
                x.xuid = string.Empty;
        });

        Server.Permissions.RemoveAll(x => string.IsNullOrEmpty(x.xuid));

        _serverProperties.SaveServerProperties(Server);
        _serverProperties.SavePermissions(Server);
        _serverProperties.SaveAllowList(Server);
        _serverProperties.SavePlayers(Server);
        _serverProperties.SaveBackupSettings(Server);
        _serverProperties.SaveUpdateSettings(Server);

        var availableAddons = _bdsAddon.GetAvailableAddons();
        foreach (var uuid in ApplyAddons)
        {
            if(server.Addons.Any(x => x.Manifest.header.uuid == uuid))
                continue;
            var addon = availableAddons.FirstOrDefault(x => x.Manifest.header.uuid == uuid);
            if(addon == null)
                continue;
            _bdsAddon.InstallAddon(addon, Server);
        }

        foreach (var addon in server.Addons)
        {
            if(ApplyAddons.Any(x => x == addon.Manifest.header.uuid))
                continue;
            _bdsAddon.UninstallAddon(addon, Server);
        }

        _optionsIO.RefreshServers();
        return Task.FromResult<IActionResult>(RedirectToPage("./Index"));
    }

    public async Task<IActionResult> OnPostUploadWorld(string path, string levelName)
    {
        var routeOptions = string.IsNullOrEmpty(path) 
            ? new { createNew = true, path = string.Empty, newAddon = false } 
            : new { createNew = false, path = path, newAddon = false };

        if(string.IsNullOrEmpty(path))
            return RedirectToPage("./Index");
        
        var server = _optionsIO.ManagerOptions.Servers.FirstOrDefault(x => x.Path == path);
        if(server == null)
            return RedirectToPage("./Index");
        
        if (UploadedFile == null)
            return RedirectToPage("./ManageServer", routeOptions);
        if (string.IsNullOrEmpty(_downloadPath))
            return RedirectToPage("./ManageServer", routeOptions);
        if (string.IsNullOrEmpty(_serversPath))
            throw new Exception("ServersPath is not set in appsettings.json");
        var destinationPath = Path.Combine(_serversPath, server.Path, "worlds", levelName);
        if(!string.IsNullOrEmpty(_serversPath))
            Directory.CreateDirectory(_serversPath);
        

        var tempPath = Path.Combine(_downloadPath, "temp", UploadedFile.FileName);
        if(!Directory.Exists(Path.Combine(_downloadPath, "temp")))
            Directory.CreateDirectory(Path.Combine(_downloadPath, "temp"));

        using (var zipStream = new FileStream(tempPath, FileMode.Create))
        {
            await UploadedFile.CopyToAsync(zipStream);
        }

        if(Directory.Exists(destinationPath))
            Directory.Delete(destinationPath, true);

        using (var fileStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read))
        {
            using (var zip = new ZipArchive(fileStream))
            {
                zip.ExtractToDirectory(destinationPath);
            }
        }
        
        
        server.Options.LevelName = levelName;
        _serverProperties.SaveServerProperties(server);
        _serverProperties.SaveAllowList(server);
        _serverProperties.SavePermissions(server);
        _serverProperties.SavePlayers(server);
        _serverProperties.SaveBackupSettings(server);
        _serverProperties.SaveUpdateSettings(server);
        _optionsIO.RefreshServers();
        
        foreach(var file in Directory.GetFiles(Path.Combine(_downloadPath, "temp")))
            System.IO.File.Delete(file);
        return RedirectToPage("./ManageServer", routeOptions);
    }

    public async Task<IActionResult> OnPostUploadAddon(string? path)
    {
        var routeOptions = string.IsNullOrEmpty(path) 
            ? new { createNew = true, path = string.Empty, newAddon = true } 
            : new { createNew = false, path = path, newAddon = true };

        var server = _optionsIO.ManagerOptions.Servers.FirstOrDefault(x => x.Path == path);
        if (UploadedFile == null)
            return RedirectToPage("./ManageServer", routeOptions);
        if (string.IsNullOrEmpty(_downloadPath))
            return RedirectToPage("./ManageServer", routeOptions);
        var tempPath = Path.Combine(_downloadPath, "temp", UploadedFile.FileName);
        if(!Directory.Exists(Path.Combine(_downloadPath, "temp")))
            Directory.CreateDirectory(Path.Combine(_downloadPath, "temp"));

        using (var fileStream = new FileStream(tempPath, FileMode.Create))
        {
            await UploadedFile.CopyToAsync(fileStream);
        }

        await _bdsAddon.SaveAddon(tempPath);
        _optionsIO.RefreshServers();

        foreach(var file in Directory.GetFiles(Path.Combine(_downloadPath, "temp")))
            System.IO.File.Delete(file);
        
        return RedirectToPage("./ManageServer", routeOptions);
    }

    public Task<IActionResult> OnPostRemoveWorld(string path, string levelName)
    {
        var routeOptions = string.IsNullOrEmpty(path) 
            ? new { createNew = true, path = string.Empty, newAddon = false } 
            : new { createNew = false, path = path, newAddon = false };

        if(string.IsNullOrEmpty(_serversPath))
            throw new Exception("ServersPath is not set in appsettings.json");

        if (string.IsNullOrEmpty(path))
            return Task.FromResult<IActionResult>(RedirectToPage("./Index"));

        var server = _optionsIO.ManagerOptions.Servers.FirstOrDefault(x => x.Path == path);
        if (server == null)
            return Task.FromResult<IActionResult>(RedirectToPage("./Index"));

        var worldPath = Path.Combine(_serversPath, server.Path, "worlds", levelName);
        if(Directory.Exists(worldPath))
            Directory.Delete(worldPath, true);

        server.Worlds = _serverProperties.ListWorlds(server);
        _optionsIO.RefreshServers();

        return Task.FromResult<IActionResult>(RedirectToPage("./ManageServer", routeOptions));
    }

    private string GetNextServerPath()
    {
        if(_serversPath == null)
            throw new Exception("ServersPath is not set in appsettings.json");
        
        if(!Directory.Exists(_serversPath))
            Directory.CreateDirectory(_serversPath);

        var dirs = Directory.GetDirectories(_serversPath).ToList().OrderByDescending(x => x);
        var lastDir = dirs.FirstOrDefault();
        if(lastDir == null)
            return "00";
        else
            return (int.Parse(lastDir.Substring(lastDir.Length - 2)) + 1).ToString("00");
    }
}