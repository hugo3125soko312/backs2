// watch_downloads.go
package main

import (
    "archive/zip"
    "bufio"
    "fmt"
    "io"
    "os"
    "path/filepath"
    "runtime"
    "strings"
    "time"
)

func main() {
    scanner := bufio.NewScanner(os.Stdin)

    homeDir, _ := os.UserHomeDir()
    defaultDownloads := filepath.Join(homeDir, "Downloads")
    defaultProject, _ := os.Getwd()

    fmt.Printf("Enter folder to watch [Press ENTER for %s]: ", defaultDownloads)
    scanner.Scan()
    watchedPath := strings.TrimSpace(scanner.Text())
    if watchedPath == "" {
        watchedPath = defaultDownloads
    }
    watchedPath = expandPath(watchedPath, homeDir)

    fmt.Printf("Enter target project folder [Press ENTER for %s]: ", defaultProject)
    scanner.Scan()
    projectPath := strings.TrimSpace(scanner.Text())
    if projectPath == "" {
        projectPath = defaultProject
    }
    projectPath = expandPath(projectPath, homeDir)

    if _, err := os.Stat(watchedPath); os.IsNotExist(err) {
        fmt.Printf("Watched path does not exist: %s\n", watchedPath)
        return
    }
    if _, err := os.Stat(projectPath); os.IsNotExist(err) {
        fmt.Printf("Target project path does not exist: %s\n", projectPath)
        return
    }

    clearScreen()
    fmt.Println("==================================================")
    fmt.Printf("[Watcher Active] Watching: '%s'\n", watchedPath)
    fmt.Printf("[Target Directory] Extracting to: '%s'\n", projectPath)
    fmt.Println("==================================================")
    fmt.Println("Waiting for new .zip downloads...\n")

    seenFiles := make(map[string]bool)
    initialEntries, _ := os.ReadDir(watchedPath)
    for _, entry := range initialEntries {
        seenFiles[entry.Name()] = true
    }

    for {
        time.Sleep(1 * time.Second)

        currentEntries, err := os.ReadDir(watchedPath)
        if err != nil {
            continue
        }

        for _, entry := range currentEntries {
            name := entry.Name()

            if strings.HasSuffix(name, ".crdownload") || strings.HasSuffix(name, ".tmp") {
                continue
            }
            if seenFiles[name] {
                continue
            }

            lowerName := strings.ToLower(name)
            if strings.HasSuffix(lowerName, ".zip") || strings.Contains(lowerName, "deepseek") {
                seenFiles[name] = true
                zipPath := filepath.Join(watchedPath, name)

                clearScreen()
                now := time.Now().Format("15:04:05")
                fmt.Printf("[%s] NEW DOWNLOAD DETECTED: %s\n", now, name)
                fmt.Println(strings.Repeat("-", 60))

                time.Sleep(1000 * time.Millisecond)

                err := processAndExtractZip(zipPath, projectPath)
                if err != nil {
                    fmt.Printf("\nExtraction failed: %v\n", err)
                    fmt.Println("\nWaiting for next file...")
                    continue
                }

                cleaned := false
                for attempt := 1; attempt <= 3; attempt++ {
                    if err := os.Remove(zipPath); err == nil {
                        cleaned = true
                        break
                    }
                    time.Sleep(500 * time.Millisecond)
                }

                if cleaned {
                    fmt.Println("\nCleanup: Removed ZIP from Downloads.")
                } else {
                    fmt.Println("\nCleanup: Could not delete ZIP automatically (file locked by browser).")
                }

                fmt.Println(strings.Repeat("-", 60))
                fmt.Println("UPDATE COMPLETE. Watching for next download...\n")
            }
        }
    }
}

func processAndExtractZip(src, dest string) error {
    r, err := zip.OpenReader(src)
    if err != nil {
        return fmt.Errorf("unable to open zip archive: %w", err)
    }
    defer r.Close()

    cleanDest := filepath.Clean(dest) + string(os.PathSeparator)

    fmt.Printf("Target Repository: %s\n", dest)
    fmt.Printf("Total entries in ZIP: %d\n\n", len(r.File))

    for i, f := range r.File {
        fpath := filepath.Join(dest, f.Name)

        if !strings.HasPrefix(filepath.Clean(fpath), filepath.Clean(dest)) && !strings.HasPrefix(fpath, cleanDest) {
            return fmt.Errorf("illegal file path in zip: %s", fpath)
        }

        if f.FileInfo().IsDir() {
            fmt.Printf("  [%d/%d] Creating Directory: %s\n", i+1, len(r.File), f.Name)
            if err := os.MkdirAll(fpath, os.ModePerm); err != nil {
                return err
            }
            continue
        }

        fmt.Printf("  [%d/%d] Extracting File: %s (%d bytes)\n", i+1, len(r.File), f.Name, f.UncompressedSize64)

        if err := os.MkdirAll(filepath.Dir(fpath), os.ModePerm); err != nil {
            return err
        }

        outFile, err := os.OpenFile(fpath, os.O_WRONLY|os.O_CREATE|os.O_TRUNC, f.Mode())
        if err != nil {
            return fmt.Errorf("failed to open output file %s: %w", fpath, err)
        }

        rc, err := f.Open()
        if err != nil {
            outFile.Close()
            return err
        }

        _, err = io.Copy(outFile, rc)
        outFile.Close()
        rc.Close()

        if err != nil {
            return fmt.Errorf("failed writing content to %s: %w", fpath, err)
        }
    }

    return nil
}

func clearScreen() {
    _ = runtime.GOOS
    fmt.Print("\033[H\033[2J")
}

func expandPath(path, home string) string {
    if strings.HasPrefix(path, "~") {
        return filepath.Join(home, path[1:])
    }
    abs, err := filepath.Abs(path)
    if err == nil {
        return abs
    }
    return path
}