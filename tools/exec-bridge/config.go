package main

import (
	"encoding/json"
	"log"
	"os"
	"regexp"
)

type FilterRule struct {
    Name    string `json:"name"`
    Pattern string `json:"pattern"`
    Reason  string `json:"reason,omitempty"`

    re *regexp.Regexp
}

type BridgeConfig struct {
    Rules []FilterRule `json:"require_approval"`
}

// DefaultRules covers git history modification and direct .git deletion.
// The user can replace or extend these in exec_bridge.config.json.
var defaultRules = []FilterRule{
    {Name: "git-commit-amend",
        Pattern: `(?i)\bgit\b[^\r\n]*\bcommit\b[^\r\n]*--amend`,
        Reason:  "rewrites the previous commit"},
    {Name: "git-rebase",
        Pattern: `(?i)\bgit\b\s+rebase\b`,
        Reason:  "rewrites commit history"},
    {Name: "git-reset-destructive",
        Pattern: `(?i)\bgit\b\s+reset\b[^\r\n]*--(hard|soft|mixed|keep|merge)\b`,
        Reason:  "moves branch pointer, may discard commits"},
    {Name: "git-push-force",
        Pattern: `(?i)\bgit\b\s+push\b[^\r\n]*(\s-f\b|\s--force\b|\s--force-with-lease\b|\s--force-if-includes\b)`,
        Reason:  "overwrites remote history"},
    {Name: "git-push-delete",
        Pattern: `(?i)\bgit\b\s+push\b[^\r\n]*(\s--delete\b|\s-d\b)`,
        Reason:  "deletes remote branch or tag"},
    {Name: "git-filter-branch",
        Pattern: `(?i)\bgit\b\s+filter-branch\b`,
        Reason:  "rewrites entire history"},
    {Name: "git-filter-repo",
        Pattern: `(?i)\bgit\b\s+filter-repo\b`,
        Reason:  "rewrites entire history"},
    {Name: "git-reflog-expire",
        Pattern: `(?i)\bgit\b\s+reflog\s+expire\b`,
        Reason:  "removes recovery points"},
    {Name: "git-gc-prune",
        Pattern: `(?i)\bgit\b\s+gc\b[^\r\n]*--prune`,
        Reason:  "deletes unreachable objects"},
    {Name: "git-branch-delete-force",
        Pattern: `(?i)\bgit\b\s+branch\b[^\r\n]*\s-D\b`,
        Reason:  "force-deletes a branch"},
    {Name: "git-tag-delete",
        Pattern: `(?i)\bgit\b\s+tag\b[^\r\n]*\s-d\b`,
        Reason:  "deletes a tag"},
    {Name: "rm-git-dir",
        Pattern: `(?i)\b(Remove-Item|rm|del|rmdir)\b[^\r\n]*\.git\b`,
        Reason:  "deletes the .git directory"},
    {Name: "write-into-git-dir",
        Pattern: `(?i)>\s*\.git[\\/]`,
        Reason:  "writes directly into .git/"},
}

func (r *FilterRule) compile() error {
    re, err := regexp.Compile(r.Pattern)
    if err != nil { return err }
    r.re = re
    return nil
}

// LoadConfig reads the config file at path. If the file doesn't exist, it
// writes a default one and returns the default ruleset. If the file exists
// but is malformed, it logs and falls back to defaults.
func LoadConfig(path string) *BridgeConfig {
    cfg := &BridgeConfig{Rules: append([]FilterRule{}, defaultRules...)}

    if data, err := os.ReadFile(path); err == nil {
        var loaded BridgeConfig
        if err := json.Unmarshal(data, &loaded); err == nil && len(loaded.Rules) > 0 {
            cfg = &loaded
        } else if err != nil {
            log.Printf("WARN: config parse failed (%v); using defaults", err)
        }
    } else if os.IsNotExist(err) {
        // Write a default config so the user has something to edit.
        if werr := writeDefaultConfig(path); werr != nil {
            log.Printf("WARN: could not write default config: %v", werr)
        } else {
            log.Printf("wrote default config to %s", path)
        }
    }

    valid := cfg.Rules[:0]
    for i := range cfg.Rules {
        if err := cfg.Rules[i].compile(); err != nil {
            log.Printf("WARN: skipping rule %q: %v", cfg.Rules[i].Name, err)
            continue
        }
        valid = append(valid, cfg.Rules[i])
    }
    cfg.Rules = valid
    return cfg
}

func writeDefaultConfig(path string) error {
    out := BridgeConfig{Rules: defaultRules}
    b, err := json.MarshalIndent(out, "", "  ")
    if err != nil { return err }
    return os.WriteFile(path, b, 0o644)
}

// RequiresApproval returns the first matching rule, or nil if the command
// passes every filter and can run immediately.
func (c *BridgeConfig) RequiresApproval(command string) *FilterRule {
    for i := range c.Rules {
        if c.Rules[i].re != nil && c.Rules[i].re.MatchString(command) {
            return &c.Rules[i]
        }
    }
    return nil
}