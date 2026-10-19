//version="2026_1019_105609"
#r "nuget: Tsinswreng.CsSh, 0.3.0-alpha"
#nullable enable
using System.Linq;
using System.Threading;
using Tsinswreng.CsSh;
using static Tsinswreng.CsSh.ShGlobal;
using CT = System.Threading.CancellationToken;

var CtSource = new CancellationTokenSource();
var Ct = CtSource.Token;
var ScriptDir = CsxDir();
var SkillsRepoDir = ScriptDir/".Tsinswreng/Skills";
var AgentsSkillsDir = ScriptDir/".agents/skills";
Pth GithubBaseUrl = "https://github.com/Tsinswreng";

// skill short name (no prefix), e.g. "csharp-code-doc"
// One skill, end to end: pull the repo if it was cloned before (has .git),
// otherwise clone it, then copy the skill content into .agents/skills/.
// A leftover empty directory counts as "not cloned yet": git clone accepts
// an existing empty target directory, so empty dirs simply get filled in.
// Git failures only warn instead of throwing, so that in parallel all-skills
// mode one broken repo does not abort the others.
async Task SyncOneSkill(string SkillName, CT Ct){
	var RepoName = "tsinswreng-"+SkillName;
	var RepoDir = SkillsRepoDir/RepoName;
	var RepoUrl = GithubBaseUrl/$"skill-{SkillName}.git";
	if( Exists(RepoDir/".git") ){
		// Repo already cloned: fast-forward it.
		Echo("[pull] "+RepoName);
		var Pull = await TryExe("git",["-C", RepoDir, "pull"], Ct);
		if(!Pull.IsSuccess){
			Echo("[warn] pull failed for "+RepoName+", continuing...");
		}
	}else{
		// Never cloned, or only an empty leftover directory: clone into it.
		Echo("[clone] "+RepoName);
		var Clone = await TryExe("git",["clone", RepoUrl, RepoDir], Ct);
		if(!Clone.IsSuccess){
			Echo("[warn] clone failed for "+RepoUrl+", continuing...");
		}
	}
	// Copy skill content to .agents/skills/
	var Src = RepoDir/RepoName;
	var Dst = AgentsSkillsDir/RepoName;
	if( IsDir(Src) ){
		await Mkdir(AgentsSkillsDir, Ct);
		Echo($"[sync] {Src} -> {Dst}");
		await Rm(Dst, Ct);
		await Cp(Src, Dst, Ct);
	}else{
		Echo($"[warn] skill inner directory not found: {Src}, skipping copy");
	}
}

await Mkdir(SkillsRepoDir, Ct);
if(Args.Count >= 1){
	// Single skill mode
	await SyncOneSkill(Args[0], Ct);
}else{
	// All skills mode: every tsinswreng-* directory under .Tsinswreng/Skills/
	// counts, including empty ones that were never cloned (SyncOneSkill will
	// clone them); all skills are synced in parallel.
	var SkillNames = LsDir(SkillsRepoDir)
		.Select(d => BaseName(d).ToString())
		.Where(Name => Name.StartsWith("tsinswreng-"))
		.Select(Name => Name["tsinswreng-".Length..])
		.ToList();
	if(SkillNames.Count == 0){
		Echo($"[warn] no skill directories found in {SkillsRepoDir}");
	}else{
		await Task.WhenAll(SkillNames.Select(Name => SyncOneSkill(Name, Ct)));
	}
}
Echo("[done]");
