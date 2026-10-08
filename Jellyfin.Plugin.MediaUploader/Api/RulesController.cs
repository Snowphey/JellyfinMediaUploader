using System.Net.Mime;
using Jellyfin.Plugin.MediaUploader.Configuration;
using Jellyfin.Plugin.MediaUploader.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaUploader.Api;

/// <summary>
/// Règles de détection (administrateurs) : lecture, enregistrement et essai sur des noms de fichiers.
/// </summary>
[ApiController]
[Route("MediaUploader")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class RulesController : MediaUploaderControllerBase
{
    /// <summary>
    /// Règles en vigueur, règles par défaut et indicateur de personnalisation.
    /// </summary>
    /// <returns>Règles.</returns>
    [HttpGet("Rules")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetRules()
    {
        if (!IsAdmin)
        {
            return Forbid();
        }

        return Ok(Describe());
    }

    /// <summary>
    /// Enregistre la liste des règles (validée : expression compilable, groupes nommés requis) ou revient aux règles par défaut.
    /// </summary>
    /// <param name="payload">Règles, ou <c>Reset</c> pour restaurer les règles par défaut.</param>
    /// <returns>Règles enregistrées.</returns>
    [HttpPut("Rules")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult SaveRules([FromBody] RulesPayload payload)
    {
        if (!IsAdmin)
        {
            return Forbid();
        }

        var config = Config;
        if (payload.Reset)
        {
            config.Rules = new List<DetectionRule>();
            config.RulesCustomized = false;
            Plugin.Instance!.UpdateConfiguration(config);
            return Ok(Describe());
        }

        var errors = new List<object>();
        var clean = new List<DetectionRule>();
        for (var i = 0; i < payload.Rules.Count; i++)
        {
            var r = payload.Rules[i];
            r.Kind = (r.Kind ?? string.Empty).Trim().ToLowerInvariant();
            r.Target = string.IsNullOrWhiteSpace(r.Target) ? "name" : r.Target.Trim().ToLowerInvariant();
            r.Name = string.IsNullOrWhiteSpace(r.Name) ? $"Règle {i + 1}" : r.Name.Trim();
            r.Id = string.IsNullOrWhiteSpace(r.Id) ? Guid.NewGuid().ToString("N")[..8] : r.Id.Trim();
            r.DefaultSeason = Math.Clamp(r.DefaultSeason, 0, 99);
            r.Description ??= string.Empty;
            var problem = DetectionEngine.Validate(r);
            if (problem is not null)
            {
                errors.Add(new { index = i, name = r.Name, message = problem });
            }

            clean.Add(r);
        }

        if (clean.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != clean.Count)
        {
            errors.Add(new { index = -1, name = string.Empty, message = "Deux règles ont le même identifiant." });
        }

        if (errors.Count > 0)
        {
            return BadRequest(new { error = "Certaines règles sont invalides : rien n'a été enregistré.", errors });
        }

        config.Rules = clean;
        config.RulesCustomized = true;
        Plugin.Instance!.UpdateConfiguration(config);
        return Ok(Describe());
    }

    /// <summary>
    /// Essaie des noms de fichiers (ou des chemins « Dossier/Fichier.ext ») sur les règles : type, titre, année, saison, épisode,
    /// règle retenue et destination. Les règles peuvent être celles de l'éditeur, pas encore enregistrées.
    /// </summary>
    /// <param name="request">Noms à essayer.</param>
    /// <returns>Un résultat par nom, et les erreurs de règles.</returns>
    [HttpPost("Rules/Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Test([FromBody] RulesTestRequest request)
    {
        if (!IsAdmin)
        {
            return Forbid();
        }

        var config = Config;
        var engine = request.Rules is null ? PlanFactory.Engine(config) : new DetectionEngine(request.Rules, config.UseFolderContext);
        var mode = PlanFactory.NormalizeMode(request.Mode) ?? "auto";

        var options = PlanFactory.Options(config);
        options.MusicRoot = "/musique";
        options.MoviesRoot = "/films";
        options.ShowsRoot = "/series";
        options.MaxFileBytes = 0;

        var files = request.Paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Take(200)
            .Select((p, i) => new PlanFile { Id = "t" + i, ClientPath = p, Size = 1, Analyzed = true })
            .ToList();
        var plan = Planner.Build(files, new Dictionary<string, ItemOverride>(), mode, options, engine, new EmptyFileProbe());
        var byId = plan.Groups.SelectMany(g => g.Items).ToDictionary(i => i.Id, StringComparer.Ordinal);

        return Ok(new
        {
            Items = files.Select(f => byId[f.Id]).ToList(),
            RuleErrors = engine.Rules.Where(r => r.Error is not null).Select(r => new { r.Rule.Id, r.Rule.Name, Message = r.Error }).ToList()
        });
    }

    private object Describe()
    {
        var config = Config;
        return new
        {
            Customized = config.RulesCustomized,
            Rules = PlanFactory.EffectiveRules(config),
            Defaults = DefaultRules.Create()
        };
    }
}
