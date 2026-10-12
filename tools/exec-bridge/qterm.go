package main

import (
	"bufio"
	"bytes"
	"encoding/json"
	"fmt"
	"net/http"
	"os"
	"strings"
	"time"
)

func renderIdle(base string) {
    fmt.Print(ansiClear)
    fmt.Printf("%s   BDS Q&A terminal%s\n", ansiGray, ansiReset)
    fmt.Printf("%s   daemon: %s%s\n", ansiGray, base, ansiReset)
    fmt.Printf("%s   waiting for questions or approvals...%s\n", ansiGray, ansiReset)
}

func renderApproval(base string, appr map[string]any, reader *bufio.Reader) {
    token, _ := appr["token"].(string)
    kind, _ := appr["kind"].(string)
    id, _ := appr["id"].(string)
    rule, _ := appr["rule"].(string)
    reason, _ := appr["reason"].(string)
    preview, _ := appr["preview"].(string)

    fmt.Print(ansiClear)
    fmt.Printf("%s   BDS Q&A terminal%s\n\n", ansiGray, ansiReset)
    fmt.Printf("%s[APPROVAL REQUIRED]%s\n", ansiYellow, ansiReset)
    fmt.Printf("%skind: %s   id: %s%s\n", ansiGray, kind, id, ansiReset)
    fmt.Printf("%srule: %s%s\n", ansiGray, rule, ansiReset)
    if reason != "" {
        fmt.Printf("%sreason: %s%s\n", ansiGray, reason, ansiReset)
    }
    fmt.Println()

    fmt.Printf("%s===============================================%s\n", ansiCyan, ansiReset)
    fmt.Printf("%s                 COMMAND PREVIEW%s\n", ansiCyan, ansiReset)
    fmt.Printf("%s===============================================%s\n", ansiCyan, ansiReset)
    for _, line := range strings.Split(preview, "\n") {
        fmt.Println(line)
    }
    fmt.Printf("%s===============================================%s\n\n", ansiCyan, ansiReset)

    fmt.Printf("%s[Enter] %sapprove%s    %s[type anything] %sdeny%s\n",
        ansiGray, ansiGreen, ansiReset, ansiGray, ansiRed, ansiReset)
    fmt.Printf("%s> %s", ansiWhite, ansiReset)

    line, err := reader.ReadString('\n')
    if err != nil { return }
    trimmed := strings.TrimRight(line, "\r\n")
    approved := trimmed == ""

    body, _ := json.Marshal(approvalAnswerRequest{Token: token, Approved: approved})
    req, _ := http.NewRequest("POST", base+"/approval/answer", bytes.NewReader(body))
    req.Header.Set("Content-Type", "application/json")
    hr, herr := http.DefaultClient.Do(req)
    if herr == nil { hr.Body.Close() }

    if approved {
        fmt.Printf("\n%s   approved%s\n", ansiGreen, ansiReset)
    } else {
        fmt.Printf("\n%s   denied: %q%s\n", ansiRed, trimmed, ansiReset)
    }
    time.Sleep(500 * time.Millisecond)
}

func fetchPendingApproval(base string) map[string]any {
    resp, err := http.Get(base + "/approval/pending")
    if err != nil { return nil }
    defer resp.Body.Close()
    var j map[string]any
    if err := json.NewDecoder(resp.Body).Decode(&j); err != nil { return nil }
    if pending, ok := j["pending"].(bool); !ok || !pending { return nil }
    return j
}

func runQTerm(host string, port int) {
    base := fmt.Sprintf("http://%s:%d", host, port)

    fmt.Printf("%sconnecting to %s ...%s\n", ansiGray, base, ansiReset)
    var ready bool
    for i := 0; i < 40; i++ {
        resp, err := http.Get(base + "/ping")
        if err == nil { resp.Body.Close(); ready = true; break }
        time.Sleep(250 * time.Millisecond)
    }
    if !ready {
        fmt.Printf("%sdaemon not reachable at %s.%s\n", ansiRed, base, ansiReset)
        fmt.Println("press Enter to exit.")
        bufio.NewReader(os.Stdin).ReadString('\n')
        return
    }

    reader := bufio.NewReader(os.Stdin)
    lastShown := ""

    renderIdle(base)

    for {
        // Approvals have priority
        if appr := fetchPendingApproval(base); appr != nil {
            renderApproval(base, appr, reader)
            renderIdle(base)
            continue
        }

        // Questions
        resp, err := http.Get(base + "/question/pending")
        if err != nil {
            fmt.Printf("\r%s[bridge unreachable]%s   ", ansiRed, ansiReset)
            time.Sleep(1000 * time.Millisecond)
            continue
        }
        var pr struct {
            Pending  bool   `json:"pending"`
            ID       string `json:"id"`
            Question string `json:"question"`
        }
        err = json.NewDecoder(resp.Body).Decode(&pr)
        resp.Body.Close()
        if err != nil { time.Sleep(500 * time.Millisecond); continue }

        if !pr.Pending || pr.ID == lastShown {
            time.Sleep(500 * time.Millisecond)
            continue
        }

        fmt.Print(ansiClear)
        fmt.Printf("%s   BDS Q&A terminal%s\n\n", ansiGray, ansiReset)
        fmt.Printf("%s[DeepSeek asks %s]%s\n", ansiCyan, pr.ID, ansiReset)
        fmt.Println(wrapText(pr.Question, 76))
        fmt.Println()
        fmt.Printf("%s   Multi-line reply. Sentinel on its own line:%s\n", ansiGray, ansiReset)
        fmt.Printf("%s     /send     submit%s\n", ansiGray, ansiReset)
        fmt.Printf("%s     /skip     skip this question%s\n", ansiGray, ansiReset)
        fmt.Printf("%s     /cancel   abandon (returns to idle)%s\n\n", ansiGray, ansiReset)

        answer, action := readMultilineAnswer(reader)

        switch action {
        case "eof":
            return
        case "cancel":
            lastShown = pr.ID
            renderIdle(base)
            continue
        case "skip":
            answer = "(skipped)"
        case "send":
            if strings.TrimSpace(answer) == "" { answer = "(empty answer)" }
        }

        body, _ := json.Marshal(answerPostRequest{ID: pr.ID, Answer: answer})
        req, _ := http.NewRequest("POST", base+"/question/answer", bytes.NewReader(body))
        req.Header.Set("Content-Type", "application/json")
        hr, e := http.DefaultClient.Do(req)
        if e != nil {
            fmt.Printf("\n%s   post failed: %s%s\n", ansiRed, e.Error(), ansiReset)
            fmt.Printf("%s   press Enter to continue.%s", ansiGray, ansiReset)
            reader.ReadString('\n')
        } else {
            hr.Body.Close()
            nLines := len(strings.Split(answer, "\n"))
            fmt.Printf("\n%s   sent %d line(s), %d char(s)%s\n", ansiGray, nLines, len(answer), ansiReset)
            time.Sleep(700 * time.Millisecond)
        }
        lastShown = pr.ID
        renderIdle(base)
    }
}

func readMultilineAnswer(reader *bufio.Reader) (string, string) {
    var lines []string
    for {
        line, err := reader.ReadString('\n')
        if err != nil {
            if len(lines) > 0 { return strings.Join(lines, "\n"), "send" }
            return "", "eof"
        }
        trimmed := strings.TrimRight(line, "\r\n")
        switch trimmed {
        case "/send":   return strings.Join(lines, "\n"), "send"
        case "/skip":   return "", "skip"
        case "/cancel": return "", "cancel"
        }
        if trimmed == "" {
            if len(lines) == 0 { continue }
            return strings.Join(lines, "\n"), "send"
        }
        lines = append(lines, trimmed)
    }
}