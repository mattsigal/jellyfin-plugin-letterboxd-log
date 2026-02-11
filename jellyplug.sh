#!/bin/bash

# jellyplug.sh - Jellyfin Plugin Updater for Synology (SynoCommunity)
# Usage: sudo ./jellyplug.sh <PluginName> [NewVersion]
# Example: sudo ./jellyplug.sh LetterboxdSync 1.1.9.0

if [ -z "$1" ]; then
    echo "Usage: sudo ./jellyplug.sh <PluginName> [NewVersion]"
    echo "Example: sudo ./jellyplug.sh LetterboxdSync 1.1.9.0"
    exit 1
fi

PLUGIN_NAME=$1
NEW_VERSION=$2
PLUGIN_BASE="/volume1/@appdata/jellyfin/data/plugins"

# Remove leading dashes if user typed --LetterboxdSync
PLUGIN_NAME=${PLUGIN_NAME#--}

# USE CURRENT DIRECTORY FOR SOURCE
SOURCE_DLL="./${PLUGIN_NAME}.dll"
# Image source (Look for letterboxd-sync.png in current dir)
SOURCE_IMG="./letterboxd-sync.png"

# 1. Find the actual plugin directory
TARGET_DIR=$(find "$PLUGIN_BASE" -maxdepth 1 -type d -name "${PLUGIN_NAME}_*" | head -n 1)

if [ -z "$TARGET_DIR" ]; then
    echo "Error: Could not find installed plugin folder for $PLUGIN_NAME in $PLUGIN_BASE"
    exit 1
fi

echo "Found Plugin Directory: $TARGET_DIR"
DEST_DLL="$TARGET_DIR/${PLUGIN_NAME}.dll"
DEST_IMG="$TARGET_DIR/icon.png"
DEST_META="$TARGET_DIR/meta.json"

# 2. Check source
if [ ! -f "$SOURCE_DLL" ]; then
    echo "Error: Source file not found at $SOURCE_DLL (Looking in current directory)"
    exit 1
fi

# 3. Backup inside the folder
if [ -f "$DEST_DLL" ]; then
    echo "Backing up existing DLL..."
    cp "$DEST_DLL" "$DEST_DLL.bak_$(date +%s)"
fi

# 4. Copy new DLL
echo "Copying new DLL..."
cp "$SOURCE_DLL" "$DEST_DLL"

# 4b. Copy Image to icon.png
if [ -f "$SOURCE_IMG" ]; then
    echo "Copying plugin image to icon.png..."
    cp "$SOURCE_IMG" "$DEST_IMG"
    chown sc-jellyfin:synocommunity "$DEST_IMG"
    chmod 644 "$DEST_IMG"
else
    echo "Warning: Image $SOURCE_IMG not found. Skipping image update."
fi

# 4c. Generate meta.json
# This ensures the plugin has proper metadata even when sideloaded
if [ ! -z "$NEW_VERSION" ]; then
    echo "Generating meta.json..."
    cat > "$DEST_META" <<EOF
{
  "name": "LetterboxdSync",
  "version": "$NEW_VERSION",
  "description": "Syncs watched movies with Letterboxd diary (Patched: IMDb Fallback)",
  "category": "General",
  "owner": "danielveigasilva",
  "imageUrl": "icon.png"
}
EOF
    chown sc-jellyfin:synocommunity "$DEST_META"
    chmod 644 "$DEST_META"
fi

# 5. Fix File Permissions
echo "Fixing DLL permissions..."
chown sc-jellyfin:synocommunity "$DEST_DLL"
chmod 644 "$DEST_DLL"

# 6. Rename Directory (If version provided)
if [ ! -z "$NEW_VERSION" ]; then
    CURRENT_DIR_NAME=$(basename "$TARGET_DIR")
    NEW_DIR_NAME="${PLUGIN_NAME}_${NEW_VERSION}"
    
    if [ "$CURRENT_DIR_NAME" != "$NEW_DIR_NAME" ]; then
        echo "Renaming directory to match new version..."
        echo "  Old: $CURRENT_DIR_NAME"
        echo "  New: $NEW_DIR_NAME"
        
        mv "$TARGET_DIR" "$PLUGIN_BASE/$NEW_DIR_NAME"
        
        # Update TARGET_DIR variable for ls check
        TARGET_DIR="$PLUGIN_BASE/$NEW_DIR_NAME"
        DEST_DLL="$TARGET_DIR/${PLUGIN_NAME}.dll"
        
        # Ensure ownership of the new folder itself is correct
        chown -R sc-jellyfin:synocommunity "$TARGET_DIR"
    else
        echo "Directory name already matches version."
    fi
fi

echo "Success! Restart Jellyfin to apply changes."
echo "Directory Contents:"
ls -la "$TARGET_DIR"
