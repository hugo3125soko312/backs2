package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"sync"
	"time"
)

// ---- exec dedup ----

type execDB struct {
    mu   sync.Mutex
    path string
    data map[string]map[string]bool
}

func newExecDB(path string) (*execDB, error) {
    d := &execDB{path: path, data: make(map[string]map[string]bool)}
    if err := d.load(); err != nil { return nil, err }
    return d, nil
}
func (d *execDB) load() error {
    b, err := os.ReadFile(d.path)
    if err != nil {
        if os.IsNotExist(err) { return nil }
        return err
    }
    return json.Unmarshal(b, &d.data)
}
func (d *execDB) saveLocked() error {
    b, err := json.MarshalIndent(d.data, "", "  ")
    if err != nil { return err }
    tmp := d.path + ".tmp"
    if err := os.WriteFile(tmp, b, 0o644); err != nil { return err }
    return os.Rename(tmp, d.path)
}
func (d *execDB) Claim(url, id string) (bool, error) {
    if url == "" || id == "" { return true, nil }
    d.mu.Lock(); defer d.mu.Unlock()
    if d.data[url] == nil { d.data[url] = make(map[string]bool) }
    if d.data[url][id] { return false, nil }
    d.data[url][id] = true
    if err := d.saveLocked(); err != nil { return false, err }
    return true, nil
}
func (d *execDB) List(url string) []string {
    if url == "" { return nil }
    d.mu.Lock(); defer d.mu.Unlock()
    ids := d.data[url]
    out := make([]string, 0, len(ids))
    for id := range ids { out = append(out, id) }
    return out
}

// ---- storage bundle ----

type storage struct {
    dir      string
    Exec     *execDB
}

func NewStorage(dir string) *storage {
    s := &storage{dir: dir}
    if dir != "" {
        db, err := newExecDB(filepath.Join(dir, "executed.json"))
        if err != nil {
            db = &execDB{data: make(map[string]map[string]bool)}
        }
        s.Exec = db
    } else {
        s.Exec = &execDB{data: make(map[string]map[string]bool)}
    }
    return s
}

// ---- question queue ----

type questionItem struct {
    URL        string
    ID         string
    Question   string
    Answer     string
    AnsweredAt time.Time
}

type questionQueue struct {
    mu        sync.Mutex
    pending   []questionItem
    answers   map[string]map[string]questionItem
    batchOpen bool
}

func newQuestionQueue() *questionQueue {
    return &questionQueue{answers: make(map[string]map[string]questionItem)}
}

func (qq *questionQueue) Enqueue(url, id, question string) {
    qq.mu.Lock()
    defer qq.mu.Unlock()
    qq.batchOpen = true
    qq.pending = append(qq.pending, questionItem{URL: url, ID: id, Question: question})
}

func (qq *questionQueue) Next() (questionItem, bool) {
    qq.mu.Lock()
    defer qq.mu.Unlock()
    if len(qq.pending) == 0 { return questionItem{}, false }
    return qq.pending[0], true
}

func (qq *questionQueue) SubmitAnswer(id, answer string) bool {
    qq.mu.Lock()
    defer qq.mu.Unlock()
    for i, item := range qq.pending {
        if item.ID == id {
            item.Answer = answer
            item.AnsweredAt = nowUTC()
            if qq.answers[item.URL] == nil { qq.answers[item.URL] = make(map[string]questionItem) }
            qq.answers[item.URL][item.ID] = item
            qq.pending = append(qq.pending[:i], qq.pending[i+1:]...)
            if len(qq.pending) == 0 { qq.batchOpen = false }
            return true
        }
    }
    return false
}

func (qq *questionQueue) Ready(url string) (bool, []questionItem) {
    qq.mu.Lock()
    defer qq.mu.Unlock()
    items := make([]questionItem, 0)
    for _, a := range qq.answers[url] { items = append(items, a) }
    return !qq.batchOpen && len(items) > 0, items
}

func (qq *questionQueue) Clear(url string) int {
    qq.mu.Lock()
    defer qq.mu.Unlock()
    n := len(qq.answers[url])
    delete(qq.answers, url)
    return n
}

// ---- approval queue ----

type approvalRequest struct {
    Token   string
    URL     string
    ID      string
    Kind    string
    Rule    string
    Reason  string
    Preview string
    Result  chan bool
    Created time.Time
}

type approvalQueue struct {
    mu      sync.Mutex
    pending []*approvalRequest
    byToken map[string]*approvalRequest
}

func newApprovalQueue() *approvalQueue {
    return &approvalQueue{byToken: make(map[string]*approvalRequest)}
}

func (aq *approvalQueue) Submit(req *approvalRequest) {
    aq.mu.Lock()
    defer aq.mu.Unlock()
    aq.pending = append(aq.pending, req)
    aq.byToken[req.Token] = req
}

func (aq *approvalQueue) Next() *approvalRequest {
    aq.mu.Lock()
    defer aq.mu.Unlock()
    if len(aq.pending) == 0 { return nil }
    return aq.pending[0]
}

func (aq *approvalQueue) Resolve(token string, approved bool) bool {
    aq.mu.Lock()
    req, ok := aq.byToken[token]
    if ok {
        delete(aq.byToken, token)
        for i, p := range aq.pending {
            if p.Token == token {
                aq.pending = append(aq.pending[:i], aq.pending[i+1:]...)
                break
            }
        }
    }
    aq.mu.Unlock()
    if !ok { return false }
    select {
    case req.Result <- approved:
    default:
    }
    return true
}

// ---- connection arbiter ----

type connState struct {
    mu       sync.Mutex
    holder   string
    lastSeen time.Time
}
func (c *connState) acquire(id string) (bool, string) {
    if id == "" { return false, "" }
    c.mu.Lock(); defer c.mu.Unlock()
    if c.holder == "" || c.holder == id || time.Since(c.lastSeen) > time.Duration(connTTL) {
        prev := c.holder
        c.holder = id
        c.lastSeen = time.Now()
        return true, prev
    }
    return false, c.holder
}
func (c *connState) release(id string) bool {
    c.mu.Lock(); defer c.mu.Unlock()
    if c.holder == id { c.holder = ""; return true }
    return false
}
func (c *connState) heartbeat(id string) bool {
    c.mu.Lock(); defer c.mu.Unlock()
    if c.holder == id { c.lastSeen = time.Now(); return true }
    return false
}
func (c *connState) current() string {
    c.mu.Lock(); defer c.mu.Unlock()
    if c.holder != "" && time.Since(c.lastSeen) > time.Duration(connTTL) { return "" }
    return c.holder
}