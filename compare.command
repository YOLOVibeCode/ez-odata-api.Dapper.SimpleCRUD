#!/bin/bash
# Double-click from Finder: run the comparison and leave the window open.
cd "$(dirname "$0")"
./compare.sh
status=$?
echo
echo "Finished with exit $status. Press Return to close."
read -r _
exit $status
