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
async Task<object?> SyncOneSkill(string SkillName, CT Ct){
	var RepoDir = SkillsRepoDir/$"tsinswreng-{SkillName}";
	var RepoUrl = GithubBaseUrl/$"skill-{SkillName}.git";
	if( IsDir(RepoDir) ){
		Echo("[pull] "+RepoUrl);
		await Exe("git",["-C", RepoDir, "pull"], Ct);
	}else{
		Echo("[clone] "+RepoUrl);
		await Exe("git",["clone", RepoUrl, RepoDir], Ct);
	}
	// Copy skill content to .agents/skills/
	var Src = RepoDir/"tsinswreng-"+SkillName;
	var Dst = AgentsSkillsDir/"tsinswreng-"+SkillName;
	if( IsDir(Src) ){
		await Mkdir(AgentsSkillsDir, Ct);
		Echo("[sync] "+Src);
		await Rm(Dst, Ct);
		await Cp(Src, Dst, Ct);
	}else{
		Echo($"[warn] skill inner dir not found: {Src}, skipping copy");
	}
	return null;
}

// Pull one existing repo and sync its content to .agents/skills/.
// Repos touch disjoint directories, and one repo's failure only warns,
// so this is safe to run in parallel across repos.
async Task SyncExistingRepo(Pth RepoDir, CT Ct){
	var RepoName = BaseName(RepoDir);
	Echo("[pull] "+RepoName);
	var Pull = await TryExe("git",["-C", RepoDir, "pull"], Ct);
	if(!Pull.IsSuccess){
		Echo("[warn] pull failed for "+RepoName+", continuing...");
	}

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
	// All skills mode: pull and sync every repo under .Tsinswreng/Skills/, all in parallel
	var RepoDirs = LsDir(SkillsRepoDir).Where(d =>
		IsDir(d/".git")
		&& BaseName(d).ToString().StartsWith("tsinswreng-")
	).ToList();
	if(RepoDirs.Count == 0){
		Echo($"[warn] no skill repos found in {SkillsRepoDir}");
	}else{
		await Task.WhenAll(RepoDirs.Select(d => SyncExistingRepo(d, Ct)));
	}
}
Echo("[done]");
