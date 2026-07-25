#!/bin/bash

count_root_specs() {
    local specs_dir="${1:-specs}"
    if [[ ! -d "$specs_dir" ]]; then
        echo "0"
        return
    fi
    find "$specs_dir" -maxdepth 1 -type f -name "*.md" 2>/dev/null | wc -l
}

is_spec_complete() {
    local spec_file="$1"
    if [[ ! -f "$spec_file" ]]; then
        return 1
    fi
    grep -qE '^(#{1,3} )?(\*\*)?Status(\*\*)?:\s+COMPLETE' "$spec_file"
}

count_incomplete_root_specs() {
    local specs_dir="${1:-specs}"
    if [[ ! -d "$specs_dir" ]]; then
        echo "0"
        return
    fi

    local count=0
    while IFS= read -r spec_file; do
        if ! is_spec_complete "$spec_file"; then
            ((count++))
        fi
    done < <(find "$specs_dir" -maxdepth 1 -type f -name "*.md" 2>/dev/null | sort)
    echo "$count"
}

get_first_incomplete_root_spec() {
    local specs_dir="${1:-specs}"
    if [[ ! -d "$specs_dir" ]]; then
        return
    fi

    while IFS= read -r spec_file; do
        if ! is_spec_complete "$spec_file"; then
            echo "$spec_file"
            return
        fi
    done < <(find "$specs_dir" -maxdepth 1 -type f -name "*.md" 2>/dev/null | sort)
}
